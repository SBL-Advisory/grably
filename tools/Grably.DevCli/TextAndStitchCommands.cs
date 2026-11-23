using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Imaging;
using Grably.Core.Imaging;
using Grably.Platform.Text;

namespace Grably.DevCli;

/// <summary>grably-dev ocr and stitch.</summary>
internal static class TextAndStitchCommands
{
    private static readonly string[] SelfTestLines =
    [
        "Grably reads text from screenshots",
        "Invoice 2026-0147 total 1,284.50 EUR",
        "Small print still counts: version 0.1.5",
    ];

    /// <summary>ocr &lt;image&gt; prints the text; ocr --selftest renders known text and checks it is read back.</summary>
    public static int Ocr(string[] args)
    {
        var ocr = new OcrService();
        if (!ocr.IsAvailable)
        {
            Console.WriteLine("FAIL no OCR language is installed for the user's languages");
            return 1;
        }

        if (args.Contains("--selftest"))
        {
            var image = RenderText(SelfTestLines, fontSize: 13);
            if (OptionValue(args, "--out") is { } saveTo)
            {
                PngWriter.Save(image, Path.GetFullPath(saveTo)); // a known text image for other checks
            }

            var outcome = ocr.RecognizeAsync(image).GetAwaiter().GetResult();
            var expected = SelfTestLines.SelectMany(l => l.Split(' ')).ToList();
            var found = outcome.Text.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var missing = expected.Where(w => !found.Contains(w)).ToList();
            Console.WriteLine($"INFO language {outcome.Language}, {outcome.Lines.Count} line(s) from a {image.Width}x{image.Height} image");
            var ok = missing.Count == 0 && outcome.Lines.Count == SelfTestLines.Length;
            Console.WriteLine(ok
                ? $"PASS ocr read all {expected.Count} words on {SelfTestLines.Length} lines"
                : $"FAIL ocr missed {missing.Count} word(s): {string.Join(", ", missing)} ({outcome.Lines.Count} line(s))");
            return ok ? 0 : 1;
        }

        if (args.Length == 0)
        {
            throw new ArgumentException("ocr expects <image file> or --selftest.");
        }

        var result = ocr.RecognizeAsync(Decode(args[0])).GetAwaiter().GetResult();
        Console.WriteLine(result.Text);
        Console.WriteLine($"PASS ocr {result.Lines.Count} line(s), language {result.Language}");
        return 0;
    }

    /// <summary>
    /// stitch --synthetic: scrolls a generated page (sticky header and footer, moving scrollbar, repeated rows) and
    /// checks the stitched image reproduces it. stitch a.png b.png ... --out f.png: stitches real frames.
    /// </summary>
    public static int Stitch(string[] args)
    {
        var output = OptionValue(args, "--out");
        if (args.Contains("--synthetic"))
        {
            var page = SyntheticPage.Create(width: 640, height: 2600);
            int[] offsets = [0, 150, 310, 470, 700, 900, 1100, 1300, 1500, 1700, 1900, 2040, 2040];
            var frames = SyntheticPage.Frames(page, viewport: 480, header: 48, footer: 32, offsets);
            var stitcher = new ScrollStitcher(frames[0]);
            foreach (var frame in frames.Skip(1))
            {
                var step = stitcher.Add(frame);
                Console.WriteLine($"INFO {step.Outcome} {step.Offset}");
            }

            var result = stitcher.Result();
            if (output is not null)
            {
                PngWriter.Save(result, Path.GetFullPath(output));
            }

            var expected = SyntheticPage.Expected(page, header: 48, footer: 32, viewport: 480, lastOffset: offsets.Max());
            var difference = SyntheticPage.Difference(result, expected, ignoreRight: 40);
            var ok = result.Height == expected.Height && difference == 0;
            Console.WriteLine(ok
                ? $"PASS stitch reproduced the {expected.Width}x{expected.Height} page from {frames.Count} frames"
                : $"FAIL stitch gave {result.Width}x{result.Height} (expected {expected.Width}x{expected.Height}), {difference} differing row(s)");
            return ok ? 0 : 1;
        }

        var files = args.Where((a, i) => a != "--out" && (i == 0 || args[i - 1] != "--out")).ToList();
        if (files.Count < 2 || output is null)
        {
            throw new ArgumentException("stitch expects --synthetic, or two or more frames and --out <file.png>.");
        }

        var real = new ScrollStitcher(Decode(files[0]));
        foreach (var file in files.Skip(1))
        {
            var step = real.Add(Decode(file));
            Console.WriteLine($"INFO {Path.GetFileName(file)}: {step.Outcome} {step.Offset}");
        }

        var stitched = real.Result();
        PngWriter.Save(stitched, Path.GetFullPath(output));
        Console.WriteLine($"PASS stitch {files.Count} frames -> {Path.GetFullPath(output)} ({stitched.Width}x{stitched.Height})");
        return 0;
    }

    private static string? OptionValue(string[] args, string name) =>
        Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

    private static PixelBuffer Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).ToPixelBuffer();
    }

    /// <summary>Black text on white, like a document on screen at 100 % scaling.</summary>
    private static PixelBuffer RenderText(IReadOnlyList<string> lines, double fontSize)
    {
        var typeface = new Typeface("Segoe UI");
        var visual = new DrawingVisual();
        const int width = 520;
        var height = (int)(lines.Count * fontSize * 2) + 24;
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (var i = 0; i < lines.Count; i++)
            {
                var text = new FormattedText(lines[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, 1);
                dc.DrawText(text, new Point(12, 12 + (i * fontSize * 2)));
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap.ToPixelBuffer();
    }
}
