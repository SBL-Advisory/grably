#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Grably.App.Capture.Overlay;
using Grably.App.Editor;
using Grably.App.Imaging;
using Grably.App.Library;
using Grably.App.ViewModels;
using Grably.App.Views;
using Grably.Core;
using Grably.Core.Annotations;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Hotkeys;
using Grably.Core.Imaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grably.App.Diagnostics;

/// <summary>
/// Debug-only: renders a window or the capture overlay to a PNG and exits, so layouts can be reviewed as images
/// without anyone looking at the screen.
/// Usage: Grably.exe --ui-snapshot (window) --out file.png [--theme dark], where window is one of
/// <see cref="Known"/>. Sample data lives in a temporary folder, never in the user's library.
/// </summary>
internal static class UiSnapshot
{
    private const string Known =
        "Library, LibraryEmpty, Settings, SettingsConflict, Editor, PreRecord, RecordingBar, OverlayHover, OverlayDrag, OverlayAdjust";

    /// <summary>Where recording windows are placed for snapshots: far off every screen.</summary>
    private static readonly PixelRect OffScreen = new(-30000, -30000, 1280, 720);

    private static Window SampleControlBar()
    {
        var bar = new Recording.RecordingControlBar(OffScreen, OffScreen, microphone: true);
        bar.Update(TimeSpan.FromSeconds(83), paused: false, muted: true);
        return bar.Window;
    }

    public static int Run(SnapshotRequest request, IServiceProvider services, ILogger logger)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"grably-snapshot-{Guid.NewGuid():N}");
        RenderTargetBitmap bitmap;
        try
        {
            bitmap = request.Window.ToLowerInvariant() switch
            {
                "library" => RenderWindow(SampleLibrary(services, scratch, withItems: true)),
                "libraryempty" => RenderWindow(SampleLibrary(services, scratch, withItems: false)),
                "settings" => RenderWindow(SampleSettings(services, conflict: false), height: 2900),
                "settingsconflict" => RenderWindow(SampleSettings(services, conflict: true), height: 2900),
                "prerecord" => RenderWindow(new Recording.PreRecordWindow(OffScreen, OffScreen, new Recording.PreRecordChoice(true, true, 30), null).Window),
                "recordingbar" => RenderWindow(SampleControlBar()),
                "overlayhover" => RenderOverlay(OverlayScenario.Hover),
                "overlaydrag" => RenderOverlay(OverlayScenario.Drag),
                "overlayadjust" => RenderOverlay(OverlayScenario.Adjust),
                "editor" => RenderWindow(SampleEditor(services)),
                _ => throw new ArgumentException($"Unknown window '{request.Window}'. Known: {Known}."),
            };
        }
        finally
        {
            SqliteConnection.ClearAllPools(); // release the sample database so its folder can go
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not delete {Folder}", scratch);
            }
        }

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

    /// <param name="height">A taller window than usual, to show a whole scrolling page in one image.</param>
    private static RenderTargetBitmap RenderWindow(Window window, double? height = null)
    {
        if (height is { } h)
        {
            window.MinHeight = window.MaxHeight = window.Height = h;
        }

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
            // Map exactly the root's own area: by default a VisualBrush stretches the content bounds, which include
            // anything overflowing the window, and squashes the picture.
            var brush = new VisualBrush(root) { Viewbox = new Rect(size), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            dc.DrawRectangle(brush, null, new Rect(size));
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

    /// <summary>The Library over a throwaway library with a few screenshots: editable, plain and missing.</summary>
    private static LibraryWindow SampleLibrary(IServiceProvider services, string folder, bool withItems)
    {
        var paths = new AppPaths(
            Path.Combine(folder, "roaming"), Path.Combine(folder, "local"), Path.Combine(folder, "Pictures"), Path.Combine(folder, "Videos"));
        var library = new LibraryService(paths, services.GetRequiredService<ILogger<LibraryService>>());
        if (withItems)
        {
            (string Title, string App, double HoursAgo, int Width, int Height, bool Editable, bool Missing)[] samples =
            [
                ("Grably_2026-11-22_15-30-05", "Code", 0.5, 1280, 800, true, false),
                ("Checkout error", "msedge", 3, 960, 600, false, false),
                ("Grably_2026-11-21_09-12-44", "ms-teams", 26, 1600, 900, true, false),
                ("Quarterly chart", "EXCEL", 50, 800, 800, false, false),
                ("Grably_2026-11-18_17-02-10", "explorer", 100, 1920, 1080, false, true),
                ("Login form", "chrome", 150, 700, 900, false, false),
            ];
            Directory.CreateDirectory(paths.DefaultScreenshotsFolder);
            Directory.CreateDirectory(paths.ProjectsFolder);
            foreach (var sample in samples)
            {
                var area = new PixelRect(0, 0, sample.Width, sample.Height);
                var window = new PixelRect(sample.Width / 8, sample.Height / 8, sample.Width * 5 / 8, sample.Height / 2);
                var image = SyntheticDesktop(area, window).ToBitmapSource();
                var file = Path.Combine(paths.DefaultScreenshotsFolder, sample.Title + ".png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(file))
                {
                    encoder.Save(stream);
                }

                var project = Path.Combine(paths.ProjectsFolder, sample.Title + ".grably");
                if (sample.Editable)
                {
                    File.WriteAllBytes(project, []);
                }

                var item = library.RecordSave(file, project, image)
                    ?? throw new InvalidOperationException("The sample library could not be written.");
                library.Repository.Update(item with
                {
                    ProjectPath = sample.Editable ? project : null,
                    SourceApp = sample.App,
                    CreatedUtc = DateTime.UtcNow.AddHours(-sample.HoursAgo),
                });
                if (sample.Missing)
                {
                    File.Delete(file);
                }
            }
        }

        var viewModel = ActivatorUtilities.CreateInstance<LibraryViewModel>(services, library);
        return ActivatorUtilities.CreateInstance<LibraryWindow>(services, viewModel);
    }

    /// <summary>The Settings window; with <paramref name="conflict"/>, as it looks when PrintScreen is taken.</summary>
    private static SettingsWindow SampleSettings(IServiceProvider services, bool conflict)
    {
        var window = services.GetRequiredService<SettingsWindow>();
        if (conflict)
        {
            var viewModel = (SettingsViewModel)window.DataContext;
            viewModel.Hotkeys.First(h => h.Action == HotkeyAction.CaptureRegion).IsTaken = true;
            viewModel.ShowPrintScreenHint = true;
            viewModel.ErrorMessage = "Some keys are already used by another app or by Windows. Pick other keys for them.";
        }

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
