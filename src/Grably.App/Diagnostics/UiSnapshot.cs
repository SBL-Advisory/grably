#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Grably.App.Capture.Overlay;
using Grably.App.Editor;
using Grably.App.Views;
using Grably.Core.Annotations;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grably.App.Diagnostics;

/// <summary>
/// Debug-only: renders a window or the capture overlay to a PNG and exits, so layouts can be reviewed as images
/// without anyone looking at the screen.
/// Usage: Grably.exe --ui-snapshot Library|OverlayHover|OverlayDrag|OverlayAdjust --out file.png [--theme dark]
/// </summary>
internal static class UiSnapshot
{
    public static int Run(SnapshotRequest request, IServiceProvider services, ILogger logger)
    {
        var bitmap = request.Window.ToLowerInvariant() switch
        {
            "library" => RenderWindow(services.GetRequiredService<LibraryWindow>()),
            "overlayhover" => RenderOverlay(OverlayScenario.Hover),
            "overlaydrag" => RenderOverlay(OverlayScenario.Drag),
            "overlayadjust" => RenderOverlay(OverlayScenario.Adjust),
            "editor" => RenderWindow(SampleEditor(services)),
            _ => throw new ArgumentException(
                $"Unknown window '{request.Window}'. Known: Library, Editor, OverlayHover, OverlayDrag, OverlayAdjust."),
        };

        var output = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(output))
        {
            encoder.Save(file);
        }

        logger.LogInformation("UI snapshot of {Window} written to {Path} ({Width}x{Height})",
            request.Window, output, bitmap.PixelWidth, bitmap.PixelHeight);
        return 0;
    }

    private static RenderTargetBitmap RenderWindow(Window window)
    {
        // Show it off-screen so layout and data binding run exactly as they would for the user.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = SystemParameters.VirtualScreenLeft - window.Width - 200;
        window.Top = SystemParameters.VirtualScreenTop;
        window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        var root = (FrameworkElement)window.Content;
        var size = new Size(Math.Ceiling(root.ActualWidth), Math.Ceiling(root.ActualHeight));
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;

        // The real window background is Mica, which only exists on screen; paint the theme background instead.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(background, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap(
            (int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        window.Close();
        return bitmap;
    }

    private enum OverlayScenario
    {
        Hover,
        Drag,
        Adjust,
    }

    /// <summary>The overlay over a synthetic 1280x800 desktop with one fake window, in a scripted state.</summary>
    private static RenderTargetBitmap RenderOverlay(OverlayScenario scenario)
    {
        var desktop = new PixelRect(0, 0, 1280, 800);
        var monitor = new MonitorInfo(0, "Synthetic", desktop, desktop with { Height = 752 }, 96, true);
        var window = new WindowInfo(1, "Sample window", 1, "sample", "SampleClass", new PixelRect(160, 120, 640, 420));
        var image = SyntheticDesktop(desktop, window.Bounds);
        var selection = new RegionSelection(desktop, [monitor], [window], adjustBeforeCapture: scenario == OverlayScenario.Adjust);

        switch (scenario)
        {
            case OverlayScenario.Hover:
                selection.MovePointer(new PixelPoint(420, 300));
                break;
            case OverlayScenario.Drag:
                selection.MovePointer(new PixelPoint(520, 260));
                selection.Press(new PixelPoint(520, 260));
                selection.MovePointer(new PixelPoint(1010, 610));
                break;
            case OverlayScenario.Adjust:
                selection.Press(new PixelPoint(520, 260));
                selection.MovePointer(new PixelPoint(1010, 610));
                selection.Release(new PixelPoint(1010, 610));
                selection.MovePointer(new PixelPoint(700, 420));
                break;
        }

        var surface = new OverlaySurface(selection, monitor, image, desktop);
        surface.Measure(new Size(desktop.Width, desktop.Height));
        surface.Arrange(new Rect(0, 0, desktop.Width, desktop.Height));
        surface.UpdateLayout();

        var bitmap = new RenderTargetBitmap(desktop.Width, desktop.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        return bitmap;
    }

    /// <summary>The editor over a synthetic screenshot with one of every annotation and the rectangle selected.</summary>
    private static EditorWindow SampleEditor(IServiceProvider services)
    {
        var area = new PixelRect(0, 0, 960, 600);
        var image = SyntheticDesktop(area, new PixelRect(80, 60, 640, 420));
        var doc = AnnotationDocument.ForImage(area.Width, area.Height);
        var rect = new RectangleItem { Rect = new RectD(120, 140, 260, 90) };
        doc.Items.Add(new RedactItem { Rect = new RectD(420, 250, 240, 40), Mode = RedactMode.Pixelate, Strength = 10 });
        doc.Items.Add(new StrokeItem
        {
            Kind = StrokeKind.Highlighter,
            Points = [new PointD(110, 330), new PointD(520, 330)],
            Style = new AnnotationStyle { Stroke = new Rgba(0xFF, 0xE6, 0x00), Thickness = 18, Opacity = 0.45, Shadow = false },
        });
        doc.Items.Add(rect);
        doc.Items.Add(new LineItem { Start = new PointD(760, 120), End = new PointD(400, 180), Heads = ArrowHeads.End });
        doc.Items.Add(new EllipseItem { Rect = new RectD(500, 360, 160, 90) });
        doc.Items.Add(new StepItem { Center = new PointD(100, 140), Number = 1 });
        doc.Items.Add(new StepItem { Center = new PointD(760, 400), Number = 2 });
        doc.Items.Add(new TextItem { Position = new PointD(740, 160), Text = "Click Save\nwhen done", Background = new Rgba(0xFF, 0xFF, 0xFF) });

        var source = new EditorDocumentSource
        {
            Original = image,
            OriginalPng = [],
            Document = doc,
            ImagePath = @"C:\Users\you\Pictures\Grably\Grably_2026-11-14_15-30-05.png",
            ProjectPath = Path.Combine(Path.GetTempPath(), "snapshot.grably"),
        };
        var window = ActivatorUtilities.CreateInstance<EditorWindow>(services, source);
        window.Width = 1100;
        window.Height = 820;
        window.Editor.Tool = EditorTool.Select;
        window.Editor.Select(rect);
        return window;
    }

    private static PixelBuffer SyntheticDesktop(PixelRect desktop, PixelRect window)
    {
        var image = new PixelBuffer(desktop.Width, desktop.Height);
        for (var y = 0; y < desktop.Height; y++)
        {
            for (var x = 0; x < desktop.Width; x++)
            {
                // Wallpaper gradient, a dark taskbar, then a window with a title bar and some "text" lines.
                var rgb = (0x20 + (x * 0x40 / desktop.Width)) << 16 | (0x50 + (y * 0x50 / desktop.Height)) << 8 | 0x90;
                if (y >= 752)
                {
                    rgb = 0x202020;
                }
                else if (window.Contains(new PixelPoint(x, y)))
                {
                    var wy = y - window.Y;
                    var wx = x - window.X;
                    rgb = wy < 32 ? 0x2B2B2B
                        : wy % 24 is >= 8 and < 14 && wx is > 24 && wx < 24 + ((wy * 37) % 400) + 120 ? 0x555555
                        : 0xF7F7F7;
                }

                image.SetRgb(x, y, rgb);
            }
        }

        // Bright marks so the loupe has something to show.
        for (var i = 0; i < 9; i++)
        {
            image.SetRgb(700 + i, 420, 0xE5383B);
            image.SetRgb(700, 420 + i, 0x0E9AA7);
        }

        return image;
    }
}
#endif
