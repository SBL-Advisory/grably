using System.Windows.Threading;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Hotkeys;
using Grably.Core.Imaging;
using Grably.Platform.Display;
using Grably.Platform.Hotkeys;

namespace Grably.App.Capture.Overlay;

/// <summary>Shows one overlay per monitor over the frozen desktop and waits for the user to pick an area.</summary>
public static class RegionOverlay
{
    private static readonly HotkeyGesture Escape = HotkeyGesture.Parse("Esc");

    /// <summary>
    /// Runs the overlay until the user picks an area or backs out. However it ends (Esc, right-click, Alt+F4 on
    /// one overlay, switching to another app), every overlay closes and the task completes, so a capture can
    /// never leave a topmost window behind or block the next capture.
    /// </summary>
    /// <param name="hotkeys">Used to grab Esc while the overlay is up, in case it could not take keyboard focus.</param>
    /// <returns>The chosen area in virtual-desktop pixels, or null when cancelled.</returns>
    public static Task<PixelRect?> RunAsync(
        RegionSelection selection, PixelBuffer desktopImage, IReadOnlyList<MonitorInfo> monitors, GlobalHotkeyService? hotkeys = null)
    {
        // Continuations (delivery: clipboard, encoding, file I/O) must not run inside a closing window's input handler.
        var done = new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = monitors.Select(m => new CaptureOverlayWindow(selection, m, desktopImage, selection.Desktop)).ToList();
        var open = new HashSet<CaptureOverlayWindow>(windows);
        var dispatcher = Dispatcher.CurrentDispatcher;
        IDisposable? escape = null;

        selection.Completed += (_, _) =>
        {
            escape?.Dispose();
            foreach (var window in windows.Where(open.Contains).ToList())
            {
                window.Close();
            }

            done.TrySetResult(selection.Result);
        };

        foreach (var window in windows)
        {
            window.Closed += (_, _) =>
            {
                open.Remove(window);
                if (selection.Phase != SelectionPhase.Done)
                {
                    selection.Cancel(); // closed some other way (Alt+F4): end the whole capture
                }
            };

            // Switching to another app (Alt+Tab, Win key) ends the capture instead of leaving a dead overlay on top.
            // Moving focus to another overlay is fine, so check once activation has settled.
            window.Deactivated += (_, _) => dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (selection.Phase != SelectionPhase.Done && !windows.Any(w => w.IsActive))
                {
                    selection.Cancel();
                }
            });
        }

        // Grab Esc before anything appears, so it works from the first frame. The cancel is queued on the UI thread,
        // so it can only run after this method has finished showing the windows.
        escape = hotkeys?.RegisterTemporary(Escape, () => dispatcher.BeginInvoke(selection.Cancel));

        try
        {
            foreach (var window in windows)
            {
                window.Show();
            }
        }
        catch
        {
            selection.Cancel(); // closes the overlays already shown and releases Esc
            throw;
        }

        var pointer = ScreenPointer.Position();
        selection.MovePointer(pointer);
        (windows.FirstOrDefault(w => w.Monitor.Bounds.Contains(pointer)) ?? windows[0]).FocusOverlay();
        return done.Task;
    }
}
