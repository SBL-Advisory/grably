using System.Text.Json.Serialization;

namespace Grably.Core.Annotations;

/// <summary>How an annotation looks. Immutable: change it with <c>with</c> and assign a new style.</summary>
public sealed record AnnotationStyle
{
    /// <summary>A clear annotation red. Grably's brand red (#E5383B) is reserved for the recording state.</summary>
    public static readonly Rgba DefaultRed = new(0xE8, 0x11, 0x23);

    public Rgba Stroke { get; init; } = DefaultRed;

    /// <summary>Line thickness in image pixels.</summary>
    public double Thickness { get; init; } = 4;

    /// <summary>Fill for shapes, or null for none.</summary>
    public Rgba? Fill { get; init; }

    /// <summary>0..1, applied to the whole item.</summary>
    public double Opacity { get; init; } = 1;

    public bool Shadow { get; init; } = true;

    /// <summary>Text size in image pixels; also sizes step badges.</summary>
    public double FontSize { get; init; } = 22;

    public string FontFamily { get; init; } = "Segoe UI";
}

/// <summary>
/// One annotation. Coordinates are image pixels of the original (uncropped) screenshot; z-order is list order.
/// Items are mutable so the editor can drag them; undo works on document snapshots.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LineItem), "line")]
[JsonDerivedType(typeof(RectangleItem), "rectangle")]
[JsonDerivedType(typeof(EllipseItem), "ellipse")]
[JsonDerivedType(typeof(StrokeItem), "stroke")]
[JsonDerivedType(typeof(TextItem), "text")]
[JsonDerivedType(typeof(StepItem), "step")]
[JsonDerivedType(typeof(RedactItem), "redact")]
public abstract class AnnotationItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AnnotationStyle Style { get; set; } = new();

    /// <summary>The area the item covers, including its stroke, for selection and hit testing.</summary>
    [JsonIgnore]
    public abstract RectD Bounds { get; }

    public abstract void Move(double dx, double dy);
}

/// <summary>An item defined by a rectangle that can be resized with handles.</summary>
public interface IRectItem
{
    RectD Rect { get; set; }
}

public enum ArrowHeads
{
    None,
    End,
    Both,
}

/// <summary>A straight line, or an arrow when it has heads.</summary>
public sealed class LineItem : AnnotationItem
{
    public PointD Start { get; set; }

    public PointD End { get; set; }

    public ArrowHeads Heads { get; set; }

    public override RectD Bounds => RectD.FromPoints(Start, End).Inflate((Style.Thickness * 2.5) + 6);

    public override void Move(double dx, double dy)
    {
        Start = Start.Offset(dx, dy);
        End = End.Offset(dx, dy);
    }
}

public sealed class RectangleItem : AnnotationItem, IRectItem
{
    public RectD Rect { get; set; }

    public double CornerRadius { get; set; }

    public override RectD Bounds => Rect.Inflate(Style.Thickness / 2);

    public override void Move(double dx, double dy) => Rect = Rect.Offset(dx, dy);
}

public sealed class EllipseItem : AnnotationItem, IRectItem
{
    public RectD Rect { get; set; }

    public override RectD Bounds => Rect.Inflate(Style.Thickness / 2);

    public override void Move(double dx, double dy) => Rect = Rect.Offset(dx, dy);
}

public enum StrokeKind
{
    Pen,

    /// <summary>Wide, translucent, flat-ended: for marking text.</summary>
    Highlighter,
}

/// <summary>A freehand pen or highlighter stroke.</summary>
public sealed class StrokeItem : AnnotationItem
{
    public List<PointD> Points { get; set; } = [];

    public StrokeKind Kind { get; set; }

    public override RectD Bounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return default;
            }

            var r = new RectD(Points[0].X, Points[0].Y, 0, 0);
            foreach (var p in Points)
            {
                r = RectD.FromPoints(new(Math.Min(r.X, p.X), Math.Min(r.Y, p.Y)), new(Math.Max(r.Right, p.X), Math.Max(r.Bottom, p.Y)));
            }

            return r.Inflate(Style.Thickness / 2);
        }
    }

    public override void Move(double dx, double dy)
    {
        for (var i = 0; i < Points.Count; i++)
        {
            Points[i] = Points[i].Offset(dx, dy);
        }
    }
}

public sealed class TextItem : AnnotationItem
{
    /// <summary>Top-left corner of the text block.</summary>
    public PointD Position { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Wrap width in image pixels, or null for no wrapping.</summary>
    public double? Width { get; set; }

    /// <summary>Optional box behind the text for readability.</summary>
    public Rgba? Background { get; set; }

    /// <summary>
    /// The laid-out size, set by the renderer (Core cannot measure fonts). Until then a rough estimate is used.
    /// </summary>
    [JsonIgnore]
    public (double Width, double Height)? MeasuredSize { get; set; }

    public override RectD Bounds
    {
        get
        {
            var (w, h) = MeasuredSize ?? Estimate();
            return new RectD(Position.X, Position.Y, w, h);
        }
    }

    public override void Move(double dx, double dy) => Position = Position.Offset(dx, dy);

    private (double, double) Estimate()
    {
        var lines = Text.Split('\n');
        var longest = lines.Max(l => l.Length);
        return (Math.Max(Style.FontSize, Width ?? (longest * Style.FontSize * 0.55)), lines.Length * Style.FontSize * 1.33);
    }
}

/// <summary>A numbered badge for step-by-step instructions.</summary>
public sealed class StepItem : AnnotationItem
{
    public PointD Center { get; set; }

    public int Number { get; set; } = 1;

    [JsonIgnore]
    public double Radius => Math.Max(8, Style.FontSize * 0.8);

    public override RectD Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override void Move(double dx, double dy) => Center = Center.Offset(dx, dy);
}

public enum RedactMode
{
    Blur,
    Pixelate,
}

/// <summary>Hides what is underneath by blurring or pixelating the original image there.</summary>
public sealed class RedactItem : AnnotationItem, IRectItem
{
    public RectD Rect { get; set; }

    public RedactMode Mode { get; set; }

    /// <summary>Blur radius or pixel block size, in image pixels.</summary>
    public int Strength { get; set; } = 10;

    public override RectD Bounds => Rect;

    public override void Move(double dx, double dy) => Rect = Rect.Offset(dx, dy);
}
