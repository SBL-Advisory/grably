using Grably.Core.Geometry;

namespace Grably.Core.Imaging;

/// <summary>
/// A 32-bit BGRA image in memory (premultiplied or opaque; screen captures are opaque with alpha 255).
/// Rows are tightly packed: stride is always Width * 4.
/// </summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height, byte[]? pixels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length != width * height * 4)
        {
            throw new ArgumentException($"Expected {width * height * 4} bytes for {width}x{height}, got {Pixels.Length}.", nameof(pixels));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    /// <summary>B, G, R, A per pixel, row by row from the top.</summary>
    public byte[] Pixels { get; }

    public PixelSize Size => new(Width, Height);

    /// <summary>The colour at (x, y) as 0xRRGGBB.</summary>
    public int GetRgb(int x, int y)
    {
        var i = (y * Stride) + (x * 4);
        return (Pixels[i + 2] << 16) | (Pixels[i + 1] << 8) | Pixels[i];
    }

    public void SetRgb(int x, int y, int rgb)
    {
        var i = (y * Stride) + (x * 4);
        Pixels[i] = (byte)rgb;
        Pixels[i + 1] = (byte)(rgb >> 8);
        Pixels[i + 2] = (byte)(rgb >> 16);
        Pixels[i + 3] = 255;
    }

    /// <summary>Copies the part of the image inside <paramref name="area"/> (in this image's own coordinates).</summary>
    /// <exception cref="ArgumentException">The area does not overlap the image.</exception>
    public PixelBuffer Crop(PixelRect area)
    {
        var clipped = area.Intersect(new PixelRect(0, 0, Width, Height));
        if (clipped.IsEmpty)
        {
            throw new ArgumentException($"Crop area {area} is outside the {Width}x{Height} image.", nameof(area));
        }

        var result = new PixelBuffer(clipped.Width, clipped.Height);
        var rowBytes = clipped.Width * 4;
        for (var row = 0; row < clipped.Height; row++)
        {
            Buffer.BlockCopy(Pixels, ((clipped.Y + row) * Stride) + (clipped.X * 4), result.Pixels, row * rowBytes, rowBytes);
        }

        return result;
    }
}
