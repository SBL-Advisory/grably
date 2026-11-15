using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Imaging;

namespace Grably.Tests.Core;

public class ImagingTests
{
    [Fact]
    public void Crop_copies_the_requested_pixels()
    {
        var image = Pattern(8, 6);

        var crop = image.Crop(new PixelRect(2, 1, 3, 4));

        Assert.Equal(new PixelSize(3, 4), crop.Size);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                Assert.Equal(image.GetRgb(x + 2, y + 1), crop.GetRgb(x, y));
            }
        }
    }

    [Fact]
    public void Crop_is_clipped_to_the_image_and_rejects_areas_outside_it()
    {
        var image = Pattern(8, 6);

        Assert.Equal(new PixelSize(3, 2), image.Crop(new PixelRect(5, 4, 10, 10)).Size);
        Assert.Throws<ArgumentException>(() => image.Crop(new PixelRect(8, 0, 2, 2)));
    }

    [Fact]
    public void Png_writer_output_decodes_to_the_same_pixels()
    {
        var image = Pattern(37, 23);
        using var stream = new MemoryStream();

        PngWriter.Write(image, stream);
        stream.Position = 0;
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var decoded = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[37 * 23 * 4];
        decoded.CopyPixels(pixels, 37 * 4, 0);

        Assert.Equal(37, decoded.PixelWidth);
        Assert.Equal(23, decoded.PixelHeight);
        Assert.Equal(image.Pixels, pixels);
    }

    [Fact]
    public void Capture_names_are_timestamped_and_never_overwrite()
    {
        var at = new DateTime(2026, 11, 14, 15, 30, 5);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("C:\\shots", "Grably_2026-11-14_15-30-05.png"),
            Path.Combine("C:\\shots", "Grably_2026-11-14_15-30-05_2.png"),
        };

        Assert.Equal(Path.Combine("C:\\shots", "Grably_2026-11-14_15-30-05.jpg"),
            CaptureNaming.UniquePath("C:\\shots", at, "jpg", taken.Contains));
        Assert.Equal(Path.Combine("C:\\shots", "Grably_2026-11-14_15-30-05_3.png"),
            CaptureNaming.UniquePath("C:\\shots", at, ".png", taken.Contains));
    }

    private static PixelBuffer Pattern(int width, int height)
    {
        var image = new PixelBuffer(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image.SetRgb(x, y, (x * 7 << 16) | (y * 11 << 8) | ((x + y) * 3));
            }
        }

        return image;
    }
}
