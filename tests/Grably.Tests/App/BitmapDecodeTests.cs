using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Imaging;

namespace Grably.Tests.App;

public class BitmapDecodeTests
{
    [Fact]
    public async Task A_frame_decoded_on_a_worker_thread_can_be_cut_up_on_another_thread()
    {
        var png = Png(64, 16);

        var frame = await Task.Run(() => BitmapConversion.Decode(png), TestContext.Current.CancellationToken);

        StaRunner.Run(() =>
        {
            // The timeline crops the thumbnail strip on the UI thread; a frame tied to its decoder's thread throws here.
            var tile = new CroppedBitmap(frame, new Int32Rect(16, 0, 16, 16));
            tile.Freeze();
            Assert.Equal(16, tile.PixelWidth);
            Assert.Equal(96, frame.DpiX);
        });
    }

    private static byte[] Png(int width, int height)
    {
        byte[] bytes = [];
        StaRunner.Run(() =>
        {
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)0x80);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 72, 72, PixelFormats.Bgra32, null, pixels, width * 4)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            bytes = stream.ToArray();
        });
        return bytes;
    }
}
