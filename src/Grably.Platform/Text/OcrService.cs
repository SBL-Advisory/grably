using System.Runtime.InteropServices.WindowsRuntime;
using Grably.Core.Geometry;
using Grably.Core.Imaging;
using Grably.Core.Text;
using Windows.Graphics.Imaging;
using OcrEngine = Windows.Media.Ocr.OcrEngine;
using OcrLine = Grably.Core.Text.OcrLine;

namespace Grably.Platform.Text;

/// <summary>Windows has no OCR language for the user's languages.</summary>
public sealed class OcrUnavailableException() : Exception(
    "Text recognition needs an OCR language. In Windows Settings, open Time & language > Language & region, "
    + "select a language, open Language options and install Optical character recognition.");

/// <summary>The text found in an image, line by line.</summary>
public sealed record OcrOutcome(IReadOnlyList<OcrLine> Lines, string Language)
{
    public string Text => OcrLayout.ToText(Lines);
}

/// <summary>
/// Reads text from images with Windows' built-in OCR in the user's profile languages, in short strips that are
/// enlarged when the image is narrow (see <see cref="OcrLayout"/>). Safe to call from any thread; recognition runs
/// one strip at a time, so a user's text capture waits at most for one strip of background indexing.
/// </summary>
public sealed class OcrService
{
    private readonly Lazy<OcrEngine?> _engine = new(OcrEngine.TryCreateFromUserProfileLanguages);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Whether an OCR language is installed for one of the user's languages.</summary>
    public bool IsAvailable => _engine.Value is not null;

    /// <exception cref="OcrUnavailableException">No OCR language is installed.</exception>
    public Task<OcrOutcome> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken = default) =>
        RecognizeAsync(
            image.Width,
            image.Height,
            tile => Task.FromResult(tile.Height == image.Height ? image : image.Crop(new PixelRect(0, tile.Top, image.Width, tile.Height))),
            cancellationToken);

    /// <summary>Images up to this many pixels are decoded once; bigger ones (tall scrolling captures) strip by strip.</summary>
    private const long WholeDecodeLimit = 16_000_000;

    /// <summary>
    /// Reads an image file (PNG, JPEG). The file is read into memory and closed at once, so saving, renaming or
    /// deleting it never waits on recognition. A huge image is decoded strip by strip, so even a 30,000-px scrolling
    /// capture never sits in memory whole as pixels.
    /// </summary>
    /// <exception cref="OcrUnavailableException">No OCR language is installed.</exception>
    public async Task<OcrOutcome> RecognizeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;
        if ((long)width * height <= WholeDecodeLimit)
        {
            var whole = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
            return await RecognizeAsync(new PixelBuffer(width, height, whole.DetachPixelData()), cancellationToken);
        }

        return await RecognizeAsync(width, height, async tile =>
        {
            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds { X = 0, Y = (uint)tile.Top, Width = (uint)width, Height = (uint)tile.Height },
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
            return new PixelBuffer(width, tile.Height, data.DetachPixelData());
        }, cancellationToken);
    }

    private async Task<OcrOutcome> RecognizeAsync(int width, int height, Func<OcrTile, Task<PixelBuffer>> readStrip, CancellationToken cancellationToken)
    {
        var engine = _engine.Value ?? throw new OcrUnavailableException();
        var max = (int)OcrEngine.MaxImageDimension;
        var scale = OcrLayout.ChooseScale(width, max);
        var results = new List<(OcrTile, IReadOnlyList<OcrLine>)>();
        foreach (var tile in OcrLayout.Tiles(height, scale, max))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Each strip is scaled on its own, so a tall image is never enlarged whole.
            var strip = await readStrip(tile);
            if (scale != 1)
            {
                strip = ImageResize.Resize(strip, Math.Max(1, (int)Math.Round(strip.Width * scale)), Math.Max(1, (int)Math.Round(strip.Height * scale)));
            }

            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                strip.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8, strip.Width, strip.Height, BitmapAlphaMode.Premultiplied);
            Windows.Media.Ocr.OcrResult result;
            await _gate.WaitAsync(cancellationToken); // the engine's documentation does not promise concurrent use
            try
            {
                result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }

            results.Add((tile, result.Lines.Select(line => ToLine(line, tile.Top, scale)).ToList()));
        }

        return new OcrOutcome(OcrLayout.Join(results), engine.RecognizerLanguage.DisplayName);
    }

    /// <summary>The line with its position in the original image's pixels.</summary>
    private static OcrLine ToLine(Windows.Media.Ocr.OcrLine line, int tileTop, double scale)
    {
        var words = line.Words;
        var top = words.Count == 0 ? 0 : words.Min(w => w.BoundingRect.Y);
        var bottom = words.Count == 0 ? 0 : words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
        return new OcrLine(line.Text, tileTop + (top / scale), tileTop + (bottom / scale));
    }
}
