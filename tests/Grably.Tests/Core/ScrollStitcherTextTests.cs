using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Imaging;
using Grably.Core.Imaging;

namespace Grably.Tests.Core;

/// <summary>
/// Scrolling real rendered text, wheel step by wheel step as the scrolling capture does: lines that differ only in a
/// number must not look alike (a log, a numbered list, the end-to-end check's Notepad page).
/// </summary>
public class ScrollStitcherTextTests
{
    private const int Header = 80;
    private const int Footer = 30;
    private const int Viewport = 760;
    private const int LineHeight = 20;

    private static readonly string[] Words =
        ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "mike"];

    [Theory]
    [InlineData(1200, true)]
    [InlineData(1920, true)]
    [InlineData(1200, false)]
    [InlineData(1920, false)]
    public void Text_scrolled_a_few_lines_at_a_time_is_stitched_with_every_offset_right(int width, bool similarLines)
    {
        var page = RenderPage(width, lines: 160, similarLines);
        var content = Viewport - Header - Footer;
        var maxScroll = page.Height - content;
        var stitcher = new ScrollStitcher(Frame(page, 0, width));
        var position = 0;
        var notches = 1;
        var perNotch = 0.0;
        var still = 0;
        while (still < 2)
        {
            var next = Math.Min(maxScroll, position + (3 * LineHeight * notches)); // a notch scrolls three lines
            var step = stitcher.Add(Frame(page, next, width), perNotch > 0 ? (int)Math.Round(perNotch * notches) : null);
            if (step.Outcome == StitchOutcome.NoMovement)
            {
                still++;
                continue;
            }

            Assert.Equal((StitchOutcome.Appended, next - position), (step.Outcome, step.Offset));
            perNotch = (double)step.Offset / notches;
            notches = Math.Clamp((int)Math.Round(content * 0.5 / perNotch), 1, 15);
            position = next;
        }

        Assert.Equal(maxScroll, position);
        var result = stitcher.Result();
        Assert.Equal(Header + page.Height + Footer, result.Height);
        Assert.Equal(page.GetRgb(40, 15), result.GetRgb(40, Header + 15)); // first line in place
        Assert.Equal(page.GetRgb(40, page.Height - 25), result.GetRgb(40, Header + page.Height - 25)); // last line too
    }

    private static PixelBuffer RenderPage(int width, int lines, bool similarLines)
    {
        PixelBuffer? page = null;
        StaRunner.Run(() =>
        {
            var random = new Random(3);
            var height = (lines * LineHeight) + 40;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                for (var i = 1; i <= lines; i++)
                {
                    var line = similarLines
                        ? string.Format(CultureInfo.InvariantCulture, "Line {0:D3} of the scrolling capture test, word{1}", i, i * 7)
                        : string.Join(' ', Enumerable.Range(0, 4 + random.Next(8)).Select(_ => Words[random.Next(Words.Length)]));
                    var text = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 15, Brushes.Black, 1);
                    dc.DrawText(text, new Point(12, 10 + ((i - 1) * LineHeight)));
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            page = bitmap.ToPixelBuffer();
        });
        return page!;
    }

    /// <summary>The window at a scroll position: a toolbar above, a status bar below, the page in between.</summary>
    private static PixelBuffer Frame(PixelBuffer page, int offset, int width)
    {
        var frame = new PixelBuffer(width, Viewport);
        var stride = width * 4;
        for (var y = 0; y < Viewport; y++)
        {
            if (y < Header || y >= Viewport - Footer)
            {
                var color = y < Header ? 0xF3F3F3 : 0xEEEEEE;
                for (var x = 0; x < width; x++)
                {
                    frame.SetRgb(x, y, color);
                }
            }
            else
            {
                Buffer.BlockCopy(page.Pixels, (offset + y - Header) * stride, frame.Pixels, y * stride, stride);
            }
        }

        return frame;
    }
}
