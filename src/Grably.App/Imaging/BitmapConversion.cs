using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.Core.Imaging;

namespace Grably.App.Imaging;

public static class BitmapConversion
{
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
