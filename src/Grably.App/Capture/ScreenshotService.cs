using Grably.App.Capture.Overlay;
using Grably.App.Shell;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Settings;
using Grably.Platform.Capture;
using Grably.Platform.Display;
using Grably.Platform.Hotkeys;
using Microsoft.Extensions.Logging;

namespace Grably.App.Capture;

/// <summary>
/// Takes a screenshot: freezes the whole virtual desktop at once, picks the area (overlay, monitor under the pointer,
/// or the foreground window), crops, and hands the result to <see cref="CaptureDelivery"/>. Runs on the UI thread.
/// </summary>
public sealed class ScreenshotService(
    SettingsStore settings, CaptureDelivery delivery, GlobalHotkeyService hotkeys, UserNotifications notifications,
    ILogger<ScreenshotService> logger)
{
    /// <summary>True while a capture is being taken (including while the overlay is open).</summary>
    public bool IsBusy => _busy;

    private bool _busy;

    /// <param name="kind">What to capture.</param>
    /// <param name="window">
    /// For <see cref="CaptureKind.Window"/>: the window to capture. Null means the current foreground window, which is
    /// right for hotkeys but not for the tray menu (it takes the foreground itself).
    /// </param>
    public async Task CaptureAsync(CaptureKind kind, nint? window = null)
    {
        if (_busy)
        {
            logger.LogInformation("{Kind} capture ignored: a capture is already in progress", kind);
            return;
        }

        _busy = true;
        try
        {
            var options = settings.Current.Screenshot;
            if (options.DelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.DelaySeconds));
            }

            // Read the foreground window before the overlay appears, then freeze the screen. Grably's own windows
            // (the Library, later the editor) are valid targets: they are in the frozen image like everything else.
            var foreground = window is { } handle ? WindowEnumerator.GetWindow(handle) : WindowEnumerator.GetForegroundWindow();
            var desktop = MonitorService.VirtualScreen;
            var monitors = MonitorService.GetMonitors();
            var image = ScreenGrabber.Grab(desktop, options.IncludeCursor);
            var capturedAt = DateTime.Now;

            PixelRect? area;
            WindowInfo? source = null;
            switch (kind)
            {
                case CaptureKind.FullScreen:
                    var pointer = ScreenPointer.Position();
                    var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(pointer))
                        ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
                    area = monitor.Bounds;
                    break;

                case CaptureKind.Window:
                    if (foreground is null)
                    {
                        notifications.Show("There is no window to capture. Click the window first, then capture.");
                        return;
                    }

                    area = foreground.Bounds.Intersect(desktop);
                    source = foreground;
                    break;

                default:
                    var windows = WindowEnumerator.GetVisibleWindows();
                    var selection = new RegionSelection(desktop, monitors, windows, options.AdjustBeforeCapture);
                    area = await RegionOverlay.RunAsync(selection, image, monitors, hotkeys);
                    source = selection.SourceWindow;
                    break;
            }

            if (area is not { IsEmpty: false } target)
            {
                logger.LogInformation("{Kind} capture cancelled", kind);
                return;
            }

            var cropped = image.Crop(target.Offset(-desktop.X, -desktop.Y));
            delivery.Deliver(new CaptureResult(cropped, target, kind, new CaptureSource(source?.Title, source?.ProcessName), capturedAt));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Kind} capture failed", kind);
            notifications.Show($"The screenshot failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }
}
