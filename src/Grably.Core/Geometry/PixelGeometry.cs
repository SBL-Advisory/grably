namespace Grably.Core.Geometry;

/// <summary>A point in physical pixels of the virtual desktop (may be negative on monitors left of or above the primary).</summary>
public readonly record struct PixelPoint(int X, int Y)
{
    public PixelPoint Offset(int dx, int dy) => new(X + dx, Y + dy);

    public override string ToString() => $"{X},{Y}";
}

public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>
/// An axis-aligned rectangle in physical pixels. Right and Bottom are exclusive, so a rectangle at (0,0) with size
/// 10x10 covers pixels 0..9 and <see cref="Right"/> is 10.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect Empty => default;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public PixelPoint Location => new(X, Y);

    public PixelSize Size => new(Width, Height);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    /// <summary>
    /// The rectangle spanned by two pixel positions, in any order, including both pixels: dragging from (5,5) to
    /// (5,5) selects one pixel; dragging from (10,10) to (0,0) selects 11x11.
    /// </summary>
    public static PixelRect FromCorners(PixelPoint a, PixelPoint b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        return FromEdges(left, top, Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
    }

    public bool Contains(PixelPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public bool Contains(PixelRect r) => !r.IsEmpty && r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;

    public bool IntersectsWith(PixelRect r) => !Intersect(r).IsEmpty;

    /// <summary>The overlapping part, or <see cref="Empty"/> when they do not overlap.</summary>
    public PixelRect Intersect(PixelRect r)
    {
        var left = Math.Max(X, r.X);
        var top = Math.Max(Y, r.Y);
        var right = Math.Min(Right, r.Right);
        var bottom = Math.Min(Bottom, r.Bottom);
        return right > left && bottom > top ? FromEdges(left, top, right, bottom) : Empty;
    }

    /// <summary>The smallest rectangle containing both. An empty rectangle contributes nothing.</summary>
    public PixelRect Union(PixelRect r)
    {
        if (IsEmpty)
        {
            return r;
        }

        if (r.IsEmpty)
        {
            return this;
        }

        return FromEdges(Math.Min(X, r.X), Math.Min(Y, r.Y), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Moves (without resizing) so the rectangle lies inside <paramref name="bounds"/> as far as it fits.</summary>
    public PixelRect MoveInside(PixelRect bounds)
    {
        var x = Math.Clamp(X, bounds.X, Math.Max(bounds.X, bounds.Right - Width));
        var y = Math.Clamp(Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - Height));
        return this with { X = x, Y = y };
    }

    /// <summary>Clamps a point to the last pixel inside the rectangle.</summary>
    public PixelPoint Clamp(PixelPoint p) =>
        new(Math.Clamp(p.X, X, Math.Max(X, Right - 1)), Math.Clamp(p.Y, Y, Math.Max(Y, Bottom - 1)));

    /// <summary>Rounds the size down to even numbers (video encoders need even dimensions).</summary>
    public PixelRect EvenSize() => this with { Width = Width & ~1, Height = Height & ~1 };

    public override string ToString() => $"{X},{Y} {Width}x{Height}";

    /// <summary>Parses "x,y,w,h" (as used by the dev harness).</summary>
    public static bool TryParse(string? text, out PixelRect rect)
    {
        rect = Empty;
        var parts = text?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 4 }
            || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h)
            || w <= 0 || h <= 0)
        {
            return false;
        }

        rect = new PixelRect(x, y, w, h);
        return true;
    }
}
