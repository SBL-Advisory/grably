using Grably.App.Editor;
using Grably.App.Imaging;
using Grably.App.Library;
using Grably.App.Shell;
using Grably.Core;
using Grably.Core.Capture;
using Grably.Core.Settings;
using Grably.Platform.Display;
using Microsoft.Extensions.Logging;

namespace Grably.App.Capture;

/// <summary>What happens to a finished screenshot: file, clipboard, library, then the editor or a preview.</summary>
public sealed class CaptureDelivery(
    SettingsStore settings, AppPaths paths, EditorService editors, LibraryService library, UserNotifications notifications,
    ILogger<CaptureDelivery> logger)
{
    /// <summary>Saves (always first, so nothing is lost) and copies the capture. Returns the saved file's path.</summary>
    public string Deliver(CaptureResult capture)
    {
        var options = settings.Current.Screenshot;
        var bitmap = capture.Image.ToBitmapSource();

        var folder = settings.Current.General.ScreenshotsFolder is { Length: > 0 } custom ? custom : paths.DefaultScreenshotsFolder;
        var path = CaptureNaming.UniquePath(folder, capture.CapturedAt, ImageOutput.Extension(options.Format));
        ImageOutput.Save(bitmap, path, options.Format, options.JpegQuality);

        logger.LogInformation(
            "{Kind} capture {Width}x{Height} at {Area} from {Process} saved to {Path}",
            capture.Kind, capture.Image.Width, capture.Image.Height, capture.Area,
            string.IsNullOrEmpty(capture.Source.ProcessName) ? "(desktop)" : capture.Source.ProcessName, path);

        var copied = false;
        if (options.CopyToClipboard)
        {
            try
            {
                ImageOutput.CopyToClipboard(bitmap);
                copied = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Usually another app (a clipboard manager, a remote desktop session) holding the clipboard. The file
                // is already saved, so this is never a failed screenshot.
                logger.LogWarning(ex, "Could not copy the screenshot to the clipboard");
            }
        }

        library.AddCapture(capture, path, bitmap);

        switch (options.AfterCapture)
        {
            case AfterCaptureAction.OpenEditor:
                editors.OpenCapture(capture, path);
                if (options.CopyToClipboard && !copied)
                {
                    notifications.Show("The clipboard was busy, so the screenshot was not copied.");
                }

                break;

            case AfterCaptureAction.ShowPreview:
                var monitor = MonitorService.GetMonitors().FirstOrDefault(m => m.Bounds.Contains(capture.Area.Center))
                    ?? MonitorService.GetMonitors()[0];
                new CapturePreviewWindow(path, bitmap, monitor.WorkArea, () => editors.OpenCapture(capture, path)).Show();
                break;

            default:
                notifications.Show(options.CopyToClipboard && !copied
                    ? $"Screenshot saved to {folder}, but the clipboard was busy so it was not copied."
                    : $"Screenshot {(copied ? "copied and " : string.Empty)}saved to {folder}");
                break;
        }

        return path;
    }
}
