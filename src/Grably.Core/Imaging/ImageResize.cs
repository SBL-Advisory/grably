namespace Grably.Core.Imaging;

public static class ImageResize
{
    /// <summary>Bilinear resize to <paramref name="width"/> x <paramref name="height"/> (smooth, good for text recognition).</summary>
    public static PixelBuffer Resize(PixelBuffer source, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width == source.Width && height == source.Height)
        {
            return new PixelBuffer(width, height, (byte[])source.Pixels.Clone());
        }

        var result = new PixelBuffer(width, height);
        var src = source.Pixels;
        var dst = result.Pixels;
        var sx = (double)source.Width / width;
        var sy = (double)source.Height / height;
        for (var y = 0; y < height; y++)
        {
            // Sample at pixel centres so the image does not shift by half a pixel.
            var fy = Math.Clamp(((y + 0.5) * sy) - 0.5, 0, source.Height - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var wy = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp(((x + 0.5) * sx) - 0.5, 0, source.Width - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, source.Width - 1);
                var wx = fx - x0;
                var i00 = ((y0 * source.Width) + x0) * 4;
                var i01 = ((y0 * source.Width) + x1) * 4;
                var i10 = ((y1 * source.Width) + x0) * 4;
                var i11 = ((y1 * source.Width) + x1) * 4;
                var o = ((y * width) + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var top = (src[i00 + c] * (1 - wx)) + (src[i01 + c] * wx);
                    var bottom = (src[i10 + c] * (1 - wx)) + (src[i11 + c] * wx);
                    dst[o + c] = (byte)Math.Round((top * (1 - wy)) + (bottom * wy));
                }
            }
        }

        return result;
    }
}
