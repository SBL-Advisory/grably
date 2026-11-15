using Grably.App.Imaging;
using Grably.App.Shell;
using Grably.Core;
using Grably.Core.Capture;
using Grably.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Grably.App.Capture;

/// <summary>What happens to a finished screenshot: file, clipboard, and (from M3/M4) the editor and the library.</summary>
public sealed class CaptureDelivery(SettingsStore settings, AppPaths paths, UserNotifications notifications, ILogger<CaptureDelivery> logger)
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

        // M3 opens the editor here when AfterCapture is OpenEditor; M4 adds the capture to the library.
        notifications.Show(options.CopyToClipboard && !copied
            ? $"Screenshot saved to {folder}, but the clipboard was busy so it was not copied."
            : $"Screenshot {(copied ? "copied and " : string.Empty)}saved to {folder}");
        return path;
    }
}
