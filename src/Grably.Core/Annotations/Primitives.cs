using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grably.Core.Annotations;

/// <summary>A point in image pixels (fractional, so freehand strokes stay smooth).</summary>
public readonly record struct PointD(double X, double Y)
{
    public PointD Offset(double dx, double dy) => new(X + dx, Y + dy);

    public double DistanceTo(PointD other) => Math.Sqrt(((X - other.X) * (X - other.X)) + ((Y - other.Y) * (Y - other.Y)));

    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);

    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
}

/// <summary>A rectangle in image pixels. Width and height are never negative after <see cref="FromPoints"/>.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public PointD Center => new(X + (Width / 2), Y + (Height / 2));

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromPoints(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public bool Contains(PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public RectD Inflate(double by) => new(X - by, Y - by, Math.Max(0, Width + (2 * by)), Math.Max(0, Height + (2 * by)));

    public RectD Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    public RectD Union(RectD other) => IsEmpty ? other : other.IsEmpty ? this
        : FromPoints(new(Math.Min(X, other.X), Math.Min(Y, other.Y)), new(Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom)));

    public RectD Intersect(RectD other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new RectD(left, top, right - left, bottom - top) : default;
    }
}

/// <summary>An sRGB colour with alpha, stored in documents as "#RRGGBB" or "#RRGGBBAA".</summary>
[JsonConverter(typeof(RgbaJsonConverter))]
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba Parse(string text) =>
        TryParse(text, out var colour) ? colour : throw new FormatException($"Not a colour: '{text}'.");

    public static bool TryParse(string? text, out Rgba colour)
    {
        colour = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is null || (hex.Length != 6 && hex.Length != 8)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        colour = hex.Length == 6
            ? new Rgba((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new Rgba((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public Rgba WithAlpha(byte alpha) => this with { A = alpha };

    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

internal sealed class RgbaJsonConverter : JsonConverter<Rgba>
{
    public override Rgba Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Rgba.TryParse(reader.GetString(), out var colour) ? colour : throw new JsonException("Expected a colour like #RRGGBB.");

    public override void Write(Utf8JsonWriter writer, Rgba value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
