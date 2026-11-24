using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.Core.Imaging;

namespace Grably.App.Imaging;

public static class BitmapConversion
{
    /// <summary>
    /// An encoded picture (BMP, PNG...), ready to use on any thread: its pixels are copied out, because a decoded
    /// frame stays tied to the thread that decoded it even when frozen. Shown at 96 DPI, one pixel per pixel.
    /// </summary>
    /// <exception cref="NotSupportedException">Not a picture WPF can read.</exception>
    public static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var stride = ((frame.PixelWidth * frame.Format.BitsPerPixel) + 31) / 32 * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, frame.Format, frame.Palette, pixels, stride);
        copy.Freeze();
        return copy;
    }

    /// <summary>
    /// Wraps a pixel buffer as a frozen WPF bitmap. Pass the monitor's DPI to have WPF draw it at exactly one
    /// image pixel per screen pixel on that monitor.
    /// </summary>
    public static BitmapSource ToBitmapSource(this PixelBuffer buffer, double dpi = 96)
    {
        var bitmap = BitmapSource.Create(buffer.Width, buffer.Height, dpi, dpi, PixelFormats.Bgra32, null, buffer.Pixels, buffer.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static PixelBuffer ToPixelBuffer(this BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var buffer = new PixelBuffer(bgra.PixelWidth, bgra.PixelHeight);
        bgra.CopyPixels(buffer.Pixels, buffer.Stride, 0);
        return buffer;
    }
}
