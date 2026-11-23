using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Imaging;
using Grably.Core.Imaging;
using Grably.Platform.Text;

namespace Grably.Tests.Platform;

/// <summary>Real Windows OCR: needs an OCR language for the user's languages, so it runs with the desktop tests.</summary>
[Trait("Category", "Desktop")]
public class OcrServiceTests
{
    private static PixelBuffer Render(int width, int height, params (string Text, int Y)[] lines)
    {
        PixelBuffer? image = null;
        StaRunner.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                foreach (var (text, y) in lines)
                {
                    var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1);
                    dc.DrawText(formatted, new Point(12, y));
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            image = bitmap.ToPixelBuffer();
        });
        return image!;
    }

    [Fact]
    public async Task Small_screen_text_is_read_line_by_line()
    {
        var image = Render(420, 80, ("Release notes for Grably", 10), ("Fixed the export button", 40));

        var outcome = await new OcrService().RecognizeAsync(image, TestContext.Current.CancellationToken);

        Assert.Equal(["Release notes for Grably", "Fixed the export button"], outcome.Lines.Select(l => l.Text));
        Assert.InRange(outcome.Lines[1].Top, 35, 50); // positions are in the original image's pixels
    }

    [Fact]
    public async Task A_page_taller_than_the_engine_allows_is_read_in_strips_without_losing_or_repeating_lines()
    {
        var image = Render(420, 25_000, ("First line at the top", 100), ("Line near a strip edge", 9_890), ("Last line at the bottom", 24_800));

        var outcome = await new OcrService().RecognizeAsync(image, TestContext.Current.CancellationToken);

        Assert.Equal(["First line at the top", "Line near a strip edge", "Last line at the bottom"], outcome.Lines.Select(l => l.Text));
    }
}
