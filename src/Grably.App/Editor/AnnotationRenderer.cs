using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Imaging;
using Grably.Core.Annotations;
using Grably.Core.Geometry;
using Grably.Core.Imaging;

namespace Grably.App.Editor;

/// <summary>
/// Draws annotations in image-pixel coordinates. The editor canvas and the flattened export both use this, so what
/// is saved is exactly what was on screen. Redaction reads the original image, which this renderer is given.
/// </summary>
public sealed class AnnotationRenderer(PixelBuffer image)
{
    private static readonly Color ShadowColor = Color.FromArgb(0x22, 0, 0, 0);
    private readonly Dictionary<string, (string Key, BitmapSource Bitmap)> _redactions = [];

    /// <summary>Renders the document over the image, cropped to its view area, at one pixel per image pixel.</summary>
    public static BitmapSource Flatten(AnnotationDocument document, PixelBuffer image)
    {
        var view = document.ViewArea.Intersect(new PixelRect(0, 0, image.Width, image.Height));
        if (view.IsEmpty)
        {
            view = new PixelRect(0, 0, image.Width, image.Height);
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-view.X, -view.Y));
            dc.DrawImage(image.ToBitmapSource(), new Rect(0, 0, image.Width, image.Height));
            var renderer = new AnnotationRenderer(image);
            foreach (var item in document.Items)
            {
                renderer.Draw(dc, item);
            }

            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(view.Width, view.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <param name="pixelsPerDip">For text shaping; 1 for exports, the screen DPI scale on screen.</param>
    public void Draw(DrawingContext dc, AnnotationItem item, double pixelsPerDip = 1)
    {
        if (item is RedactItem redact)
        {
            DrawRedaction(dc, redact); // always fully opaque: a see-through blur would reveal what it hides
            return;
        }

        dc.PushOpacity(Math.Clamp(item.Style.Opacity, 0.05, 1));
        try
        {
            if (item.Style.Shadow && item is not StrokeItem { Kind: StrokeKind.Highlighter })
            {
                // A soft shadow without bitmap effects: the same shape a few times, offset and very transparent.
                for (var i = 1; i <= 3; i++)
                {
                    dc.PushTransform(new TranslateTransform(i * 0.9, i * 0.9));
                    DrawBody(dc, item, ShadowColor, pixelsPerDip);
                    dc.Pop();
                }
            }

            DrawBody(dc, item, null, pixelsPerDip);
        }
        finally
        {
            dc.Pop();
        }
    }

    /// <summary>Drops cached redactions of items that no longer exist.</summary>
    public void ForgetRemoved(IReadOnlyCollection<AnnotationItem> items)
    {
        if (_redactions.Count == 0)
        {
            return;
        }

        var live = items.Select(i => i.Id).ToHashSet();
        foreach (var id in _redactions.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _redactions.Remove(id);
        }
    }

    /// <summary>Text needs its laid-out size for hit testing; measure without drawing.</summary>
    public static void Measure(TextItem text, double pixelsPerDip = 1) => _ = Layout(text, Brushes.Black, pixelsPerDip);

    private static void DrawBody(DrawingContext dc, AnnotationItem item, Color? shadow, double pixelsPerDip)
    {
        var style = item.Style;
        var stroke = Freeze(new SolidColorBrush(shadow ?? ToColor(style.Stroke)));
        switch (item)
        {
            case LineItem line:
                DrawLine(dc, line, stroke);
                break;

            case RectangleItem rect:
            {
                var fill = style.Fill is { } f ? Freeze(new SolidColorBrush(shadow ?? ToColor(f))) : null;
                dc.DrawRoundedRectangle(fill, StrokePen(stroke, style.Thickness), ToRect(rect.Rect), rect.CornerRadius, rect.CornerRadius);
                break;
            }

            case EllipseItem ellipse:
            {
                var fill = style.Fill is { } f ? Freeze(new SolidColorBrush(shadow ?? ToColor(f))) : null;
                var r = ellipse.Rect;
                dc.DrawEllipse(fill, StrokePen(stroke, style.Thickness), new Point(r.Center.X, r.Center.Y), r.Width / 2, r.Height / 2);
                break;
            }

            case StrokeItem freehand:
                DrawFreehand(dc, freehand, stroke);
                break;

            case TextItem text:
                DrawText(dc, text, shadow, pixelsPerDip);
                break;

            case StepItem step:
                DrawStep(dc, step, stroke, shadow is not null, pixelsPerDip);
                break;
        }
    }

    private static void DrawLine(DrawingContext dc, LineItem line, Brush brush)
    {
        var t = line.Style.Thickness;
        var start = new Point(line.Start.X, line.Start.Y);
        var end = new Point(line.End.X, line.End.Y);
        var direction = end - start;
        var length = direction.Length;
        if (length < 0.5)
        {
            return;
        }

        direction /= length;
        var headLength = Math.Min((3.5 * t) + 8, length * 0.6);
        var headHalfWidth = (1.6 * t) + 5;

        // The shaft stops at the base of each head so its round cap never pokes through the point.
        var shaftStart = line.Heads == ArrowHeads.Both ? start + (direction * headLength * 0.8) : start;
        var shaftEnd = line.Heads != ArrowHeads.None ? end - (direction * headLength * 0.8) : end;
        dc.DrawLine(StrokePen(brush, t), shaftStart, shaftEnd);

        if (line.Heads != ArrowHeads.None)
        {
            dc.DrawGeometry(brush, null, ArrowHead(end, direction, headLength, headHalfWidth));
        }

        if (line.Heads == ArrowHeads.Both)
        {
            dc.DrawGeometry(brush, null, ArrowHead(start, -direction, headLength, headHalfWidth));
        }
    }

    private static Geometry ArrowHead(Point tip, Vector direction, double length, double halfWidth)
    {
        var normal = new Vector(-direction.Y, direction.X);
        var baseCentre = tip - (direction * length);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tip, isFilled: true, isClosed: true);
            g.LineTo(baseCentre + (normal * halfWidth), isStroked: false, isSmoothJoin: false);
            g.LineTo(baseCentre - (normal * halfWidth), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void DrawFreehand(DrawingContext dc, StrokeItem stroke, Brush brush)
    {
        var points = stroke.Points;
        if (points.Count == 0)
        {
            return;
        }

        var highlighter = stroke.Kind == StrokeKind.Highlighter;
        var pen = new Pen(brush, stroke.Style.Thickness)
        {
            StartLineCap = highlighter ? PenLineCap.Flat : PenLineCap.Round,
            EndLineCap = highlighter ? PenLineCap.Flat : PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        if (points.Count == 1)
        {
            var p = new Point(points[0].X, points[0].Y);
            dc.DrawLine(pen, p, p + new Vector(0.01, 0)); // a dot
            return;
        }

        // Smooth through the midpoints between samples (quadratic curves), which removes mouse jitter.
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToPoint(points[0]), isFilled: false, isClosed: false);
            for (var i = 1; i < points.Count - 1; i++)
            {
                var mid = new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2);
                g.QuadraticBezierTo(ToPoint(points[i]), mid, isStroked: true, isSmoothJoin: true);
            }

            g.LineTo(ToPoint(points[^1]), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawText(DrawingContext dc, TextItem text, Color? shadow, double pixelsPerDip)
    {
        var brush = Freeze(new SolidColorBrush(shadow ?? ToColor(text.Style.Stroke)));
        var (formatted, padding) = Layout(text, brush, pixelsPerDip);
        var box = new Rect(text.Position.X, text.Position.Y, formatted.WidthIncludingTrailingWhitespace + (2 * padding), formatted.Height + (2 * padding));
        if (text.Background is { } background)
        {
            var fill = Freeze(new SolidColorBrush(shadow ?? ToColor(background)));
            dc.DrawRoundedRectangle(fill, null, box, padding, padding);
        }
        else if (shadow is not null)
        {
            return; // without a box, a text shadow only blurs the letters
        }

        dc.DrawText(formatted, new Point(text.Position.X + padding, text.Position.Y + padding));
    }

    private static (FormattedText Text, double Padding) Layout(TextItem text, Brush brush, double pixelsPerDip)
    {
        var style = text.Style;
        var formatted = new FormattedText(
            text.Text.Length == 0 ? " " : text.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(style.FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            Math.Max(4, style.FontSize),
            brush,
            pixelsPerDip);
        var padding = Math.Round(style.FontSize * 0.25);
        if (text.Width is { } width && width > 2 * padding)
        {
            formatted.MaxTextWidth = width - (2 * padding);
        }

        text.MeasuredSize = (formatted.WidthIncludingTrailingWhitespace + (2 * padding), formatted.Height + (2 * padding));
        return (formatted, padding);
    }

    private static void DrawStep(DrawingContext dc, StepItem step, Brush fill, bool isShadow, double pixelsPerDip)
    {
        var centre = new Point(step.Center.X, step.Center.Y);
        dc.DrawEllipse(fill, isShadow ? null : StrokePen(Brushes.White, Math.Max(1.5, step.Radius * 0.08)), centre, step.Radius, step.Radius);
        if (isShadow)
        {
            return;
        }

        var number = new FormattedText(
            step.Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(step.Style.FontFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            step.Radius * (step.Number >= 10 ? 0.95 : 1.15),
            Brushes.White,
            pixelsPerDip);
        dc.DrawText(number, new Point(centre.X - (number.Width / 2), centre.Y - (number.Height / 2)));
    }

    private void DrawRedaction(DrawingContext dc, RedactItem redact)
    {
        var area = ToPixelRect(redact.Rect).Intersect(new PixelRect(0, 0, image.Width, image.Height));
        if (area.IsEmpty)
        {
            return;
        }

        var key = $"{area}|{redact.Mode}|{redact.Strength}";
        if (!_redactions.TryGetValue(redact.Id, out var cached) || cached.Key != key)
        {
            cached = (key, Redact(area, redact.Mode, Math.Max(1, redact.Strength)));
            _redactions[redact.Id] = cached;
        }

        dc.DrawImage(cached.Bitmap, new Rect(area.X, area.Y, area.Width, area.Height));
    }

    private BitmapSource Redact(PixelRect area, RedactMode mode, int strength)
    {
        if (mode == RedactMode.Pixelate)
        {
            return ImageFilters.Pixelate(image.Crop(area), strength).ToBitmapSource();
        }

        // Blur a margin around the area too, so the edges blend with real pixels instead of clamped copies.
        var margin = strength * 3;
        var padded = new PixelRect(area.X - margin, area.Y - margin, area.Width + (2 * margin), area.Height + (2 * margin))
            .Intersect(new PixelRect(0, 0, image.Width, image.Height));
        var blurred = ImageFilters.FastBlur(image.Crop(padded), strength);
        return blurred.Crop(area.Offset(-padded.X, -padded.Y)).ToBitmapSource();
    }

    private static Pen StrokePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, Math.Max(0.5, thickness)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static PixelRect ToPixelRect(RectD r) =>
        PixelRect.FromEdges((int)Math.Floor(r.X), (int)Math.Floor(r.Y), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));

    private static Rect ToRect(RectD r) => new(r.X, r.Y, r.Width, r.Height);

    private static Point ToPoint(PointD p) => new(p.X, p.Y);

    public static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
