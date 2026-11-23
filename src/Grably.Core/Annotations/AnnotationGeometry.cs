namespace Grably.Core.Annotations;

/// <summary>Where on a selected item the pointer is: a resize handle, a line end, or the body (move).</summary>
public enum Handle
{
    None,
    Body,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    Start,
    End,
}

/// <summary>Hit testing, handles and drag maths for annotations, in image pixels. Pure, so it is unit-tested.</summary>
public static class AnnotationGeometry
{
    public static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared == 0)
        {
            return p.DistanceTo(a);
        }

        var t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return p.DistanceTo(new PointD(a.X + (t * dx), a.Y + (t * dy)));
    }

    /// <summary>Whether a press at <paramref name="p"/> picks <paramref name="item"/>. Unfilled shapes are picked on their outline.</summary>
    public static bool HitTest(AnnotationItem item, PointD p, double tolerance)
    {
        var reach = (item.Style.Thickness / 2) + tolerance;
        switch (item)
        {
            case LineItem line:
                return DistanceToSegment(p, line.Start, line.End) <= reach + (line.Heads != ArrowHeads.None ? item.Style.Thickness : 0);

            case RectangleItem rect:
                return rect.Style.Fill is not null
                    ? rect.Rect.Inflate(reach).Contains(p)
                    : rect.Rect.Inflate(reach).Contains(p) && !rect.Rect.Inflate(-reach).Contains(p);

            case EllipseItem ellipse:
            {
                var r = ellipse.Rect;
                var rx = Math.Max(r.Width / 2, 0.5);
                var ry = Math.Max(r.Height / 2, 0.5);
                var c = r.Center;
                var nx = (p.X - c.X) / rx;
                var ny = (p.Y - c.Y) / ry;
                var radial = Math.Sqrt((nx * nx) + (ny * ny)); // 1 on the outline
                var distance = Math.Abs(radial - 1) * Math.Min(rx, ry);
                return distance <= reach || (ellipse.Style.Fill is not null && radial <= 1);
            }

            case StrokeItem stroke:
                if (stroke.Points.Count == 1)
                {
                    return p.DistanceTo(stroke.Points[0]) <= reach;
                }

                for (var i = 1; i < stroke.Points.Count; i++)
                {
                    if (DistanceToSegment(p, stroke.Points[i - 1], stroke.Points[i]) <= reach)
                    {
                        return true;
                    }
                }

                return false;

            case StepItem step:
                return p.DistanceTo(step.Center) <= step.Radius + tolerance;

            default: // text and redaction: the whole box
                return item.Bounds.Inflate(tolerance).Contains(p);
        }
    }

    /// <summary>The topmost item under <paramref name="p"/>, or null.</summary>
    public static AnnotationItem? TopmostAt(IReadOnlyList<AnnotationItem> items, PointD p, double tolerance)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (HitTest(items[i], p, tolerance))
            {
                return items[i];
            }
        }

        return null;
    }

    /// <summary>The handles drawn on a selected item.</summary>
    public static IReadOnlyList<(Handle Handle, PointD Position)> Handles(AnnotationItem item)
    {
        switch (item)
        {
            case LineItem line:
                return [(Handle.Start, line.Start), (Handle.End, line.End)];
            case IRectItem { Rect: var r }:
                var mx = r.X + (r.Width / 2);
                var my = r.Y + (r.Height / 2);
                return
                [
                    (Handle.TopLeft, new(r.X, r.Y)), (Handle.Top, new(mx, r.Y)), (Handle.TopRight, new(r.Right, r.Y)),
                    (Handle.Right, new(r.Right, my)), (Handle.BottomRight, new(r.Right, r.Bottom)),
                    (Handle.Bottom, new(mx, r.Bottom)), (Handle.BottomLeft, new(r.X, r.Bottom)), (Handle.Left, new(r.X, my)),
                ];
            default:
                return [];
        }
    }

    /// <summary>The handle at <paramref name="p"/> on a selected item, Body when inside it, or None.</summary>
    public static Handle HandleAt(AnnotationItem item, PointD p, double radius, double tolerance)
    {
        foreach (var (handle, position) in Handles(item))
        {
            if (Math.Abs(p.X - position.X) <= radius && Math.Abs(p.Y - position.Y) <= radius)
            {
                return handle;
            }
        }

        return HitTest(item, p, tolerance) || item.Bounds.Contains(p) ? Handle.Body : Handle.None;
    }

    /// <summary>
    /// Applies a drag of <paramref name="handle"/> to <paramref name="pointer"/>, starting from <paramref name="original"/>
    /// (a copy of the item when the drag began). Shift (<paramref name="constrain"/>) keeps lines at 45 degree steps
    /// and shapes square. Body drags move by the pointer delta from <paramref name="pressedAt"/>.
    /// </summary>
    public static void Drag(AnnotationItem item, AnnotationItem original, Handle handle, PointD pressedAt, PointD pointer, bool constrain)
    {
        if (handle == Handle.Body)
        {
            var delta = pointer - pressedAt;
            if (constrain)
            {
                delta = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? delta with { Y = 0 } : delta with { X = 0 };
            }

            CopyGeometry(original, item);
            item.Move(delta.X, delta.Y);
            return;
        }

        switch (item)
        {
            case LineItem line when original is LineItem from:
                if (handle == Handle.Start)
                {
                    line.Start = constrain ? SnapAngle(from.End, pointer) : pointer;
                }
                else if (handle == Handle.End)
                {
                    line.End = constrain ? SnapAngle(from.Start, pointer) : pointer;
                }

                break;

            case IRectItem rectItem when original is IRectItem { Rect: var r }:
                double left = r.X, top = r.Y, right = r.Right, bottom = r.Bottom;
                if (handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft)
                {
                    left = pointer.X;
                }

                if (handle is Handle.TopRight or Handle.Right or Handle.BottomRight)
                {
                    right = pointer.X;
                }

                if (handle is Handle.TopLeft or Handle.Top or Handle.TopRight)
                {
                    top = pointer.Y;
                }

                if (handle is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight)
                {
                    bottom = pointer.Y;
                }

                var resized = RectD.FromPoints(new(left, top), new(right, bottom));
                if (constrain && handle is Handle.TopLeft or Handle.TopRight or Handle.BottomLeft or Handle.BottomRight)
                {
                    var anchor = new PointD(
                        handle is Handle.TopLeft or Handle.BottomLeft ? r.Right : r.X,
                        handle is Handle.TopLeft or Handle.TopRight ? r.Bottom : r.Y);
                    resized = Square(anchor, pointer);
                }

                rectItem.Rect = resized;
                break;
        }
    }

    /// <summary>Snaps <paramref name="p"/> so the line from <paramref name="origin"/> is at a multiple of 45 degrees.</summary>
    public static PointD SnapAngle(PointD origin, PointD p)
    {
        var dx = p.X - origin.X;
        var dy = p.Y - origin.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new PointD(origin.X + (Math.Cos(angle) * length), origin.Y + (Math.Sin(angle) * length));
    }

    /// <summary>The square from <paramref name="anchor"/> towards <paramref name="p"/>, sized by the longer side.</summary>
    public static RectD Square(PointD anchor, PointD p)
    {
        var side = Math.Max(Math.Abs(p.X - anchor.X), Math.Abs(p.Y - anchor.Y));
        return RectD.FromPoints(anchor, new(anchor.X + (Math.Sign(p.X - anchor.X) * side), anchor.Y + (Math.Sign(p.Y - anchor.Y) * side)));
    }

    private static void CopyGeometry(AnnotationItem from, AnnotationItem to)
    {
        switch (to)
        {
            case LineItem line when from is LineItem f:
                line.Start = f.Start;
                line.End = f.End;
                break;
            case IRectItem rect when from is IRectItem f:
                rect.Rect = f.Rect;
                break;
            case StrokeItem stroke when from is StrokeItem f:
                stroke.Points = [.. f.Points];
                break;
            case TextItem text when from is TextItem f:
                text.Position = f.Position;
                break;
            case StepItem step when from is StepItem f:
                step.Center = f.Center;
                break;
        }
    }
}
