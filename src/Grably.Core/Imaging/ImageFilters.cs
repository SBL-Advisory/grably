namespace Grably.Core.Imaging;

/// <summary>Redaction filters. They return new buffers and never touch the input.</summary>
public static class ImageFilters
{
    /// <summary>Averages each <paramref name="blockSize"/> x <paramref name="blockSize"/> block into one colour.</summary>
    public static PixelBuffer Pixelate(PixelBuffer source, int blockSize)
    {
        blockSize = Math.Max(1, blockSize);
        var result = new PixelBuffer(source.Width, source.Height);
        for (var by = 0; by < source.Height; by += blockSize)
        {
            for (var bx = 0; bx < source.Width; bx += blockSize)
            {
                var w = Math.Min(blockSize, source.Width - bx);
                var h = Math.Min(blockSize, source.Height - by);
                long b = 0, g = 0, r = 0, a = 0;
                for (var y = by; y < by + h; y++)
                {
                    var i = (y * source.Stride) + (bx * 4);
                    for (var x = 0; x < w; x++, i += 4)
                    {
                        b += source.Pixels[i];
                        g += source.Pixels[i + 1];
                        r += source.Pixels[i + 2];
                        a += source.Pixels[i + 3];
                    }
                }

                var n = w * h;
                for (var y = by; y < by + h; y++)
                {
                    var i = (y * result.Stride) + (bx * 4);
                    for (var x = 0; x < w; x++, i += 4)
                    {
                        result.Pixels[i] = (byte)(b / n);
                        result.Pixels[i + 1] = (byte)(g / n);
                        result.Pixels[i + 2] = (byte)(r / n);
                        result.Pixels[i + 3] = (byte)(a / n);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Approximates a Gaussian blur with three horizontal and vertical box-blur passes; edges are clamped.
    /// Strong enough to make text unreadable from radius ~6 at normal UI sizes.
    /// </summary>
    public static PixelBuffer Blur(PixelBuffer source, int radius)
    {
        if (radius < 1)
        {
            return new PixelBuffer(source.Width, source.Height, (byte[])source.Pixels.Clone());
        }

        var a = (byte[])source.Pixels.Clone();
        var b = new byte[a.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxPass(a, b, source.Width, source.Height, radius, horizontal: true);
            BoxPass(b, a, source.Width, source.Height, radius, horizontal: false);
        }

        return new PixelBuffer(source.Width, source.Height, a);
    }

    /// <summary>
    /// A blur strong enough for redaction at any radius, fast enough to redraw while dragging: large radii are
    /// computed on a block-averaged copy and scaled back up with bilinear interpolation.
    /// </summary>
    public static PixelBuffer FastBlur(PixelBuffer source, int radius)
    {
        var factor = Math.Max(1, radius / 3);
        if (factor == 1)
        {
            return Blur(source, radius);
        }

        var small = Downscale(source, factor);
        return ResizeBilinear(Blur(small, Math.Max(1, radius / factor)), source.Width, source.Height);
    }

    /// <summary>Averages <paramref name="factor"/> x <paramref name="factor"/> blocks into single pixels.</summary>
    public static PixelBuffer Downscale(PixelBuffer source, int factor)
    {
        factor = Math.Max(1, factor);
        var w = (source.Width + factor - 1) / factor;
        var h = (source.Height + factor - 1) / factor;
        var result = new PixelBuffer(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                long b = 0, g = 0, r = 0, a = 0, n = 0;
                for (var sy = y * factor; sy < Math.Min(source.Height, (y + 1) * factor); sy++)
                {
                    var i = (sy * source.Stride) + (x * factor * 4);
                    for (var sx = x * factor; sx < Math.Min(source.Width, (x + 1) * factor); sx++, i += 4)
                    {
                        b += source.Pixels[i];
                        g += source.Pixels[i + 1];
                        r += source.Pixels[i + 2];
                        a += source.Pixels[i + 3];
                        n++;
                    }
                }

                var o = (y * result.Stride) + (x * 4);
                result.Pixels[o] = (byte)(b / n);
                result.Pixels[o + 1] = (byte)(g / n);
                result.Pixels[o + 2] = (byte)(r / n);
                result.Pixels[o + 3] = (byte)(a / n);
            }
        }

        return result;
    }

    /// <summary>Resizes with bilinear interpolation (pixel centres aligned).</summary>
    public static PixelBuffer ResizeBilinear(PixelBuffer source, int width, int height)
    {
        var result = new PixelBuffer(width, height);
        var sx = (double)source.Width / width;
        var sy = (double)source.Height / height;
        for (var y = 0; y < height; y++)
        {
            var fy = Math.Clamp(((y + 0.5) * sy) - 0.5, 0, source.Height - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, source.Height - 1);
            var ty = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp(((x + 0.5) * sx) - 0.5, 0, source.Width - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, source.Width - 1);
                var tx = fx - x0;
                var o = (y * result.Stride) + (x * 4);
                for (var c = 0; c < 4; c++)
                {
                    var top = (source.Pixels[(y0 * source.Stride) + (x0 * 4) + c] * (1 - tx)) + (source.Pixels[(y0 * source.Stride) + (x1 * 4) + c] * tx);
                    var bottom = (source.Pixels[(y1 * source.Stride) + (x0 * 4) + c] * (1 - tx)) + (source.Pixels[(y1 * source.Stride) + (x1 * 4) + c] * tx);
                    result.Pixels[o + c] = (byte)Math.Round((top * (1 - ty)) + (bottom * ty));
                }
            }
        }

        return result;
    }

    private static void BoxPass(byte[] src, byte[] dst, int width, int height, int radius, bool horizontal)
    {
        var lines = horizontal ? height : width;
        var length = horizontal ? width : height;
        var step = horizontal ? 4 : width * 4;
        var window = (2 * radius) + 1;
        Span<int> sum = stackalloc int[4];
        for (var line = 0; line < lines; line++)
        {
            var start = horizontal ? line * width * 4 : line * 4;
            sum.Clear();

            // Prime the window around position 0 with clamped edges.
            for (var k = -radius; k <= radius; k++)
            {
                var idx = start + (Math.Clamp(k, 0, length - 1) * step);
                for (var c = 0; c < 4; c++)
                {
                    sum[c] += src[idx + c];
                }
            }

            for (var pos = 0; pos < length; pos++)
            {
                var o = start + (pos * step);
                for (var c = 0; c < 4; c++)
                {
                    dst[o + c] = (byte)(sum[c] / window);
                }

                var outIdx = start + (Math.Clamp(pos - radius, 0, length - 1) * step);
                var inIdx = start + (Math.Clamp(pos + radius + 1, 0, length - 1) * step);
                for (var c = 0; c < 4; c++)
                {
                    sum[c] += src[inIdx + c] - src[outIdx + c];
                }
            }
        }
    }
}
