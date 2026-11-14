#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Grably.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grably.App.Diagnostics;

/// <summary>
/// Debug-only: renders a window with its current data to a PNG and exits, so layouts can be reviewed as images
/// without anyone looking at the screen. Usage: Grably.exe --ui-snapshot Library --out library.png [--theme dark]
/// </summary>
internal static class UiSnapshot
{
    public static int Run(SnapshotRequest request, IServiceProvider services, ILogger logger)
    {
        Window window = request.Window.ToLowerInvariant() switch
        {
            "library" => services.GetRequiredService<LibraryWindow>(),
            _ => throw new ArgumentException($"Unknown window '{request.Window}'. Known: Library."),
        };

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

        var output = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(output))
        {
            encoder.Save(file);
        }

        window.Close();
        logger.LogInformation("UI snapshot of {Window} written to {Path} ({Width}x{Height})",
            request.Window, output, bitmap.PixelWidth, bitmap.PixelHeight);
        return 0;
    }
}
#endif
