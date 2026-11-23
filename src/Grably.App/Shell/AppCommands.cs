using System.Windows;
using Grably.App.Capture;
using Grably.App.Views;
using Grably.Core.Capture;
using Grably.Core.Hotkeys;
using Grably.Platform.Capture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grably.App.Shell;

/// <summary>
/// Every user-facing action, whether it comes from a hotkey, the tray menu or a window. Call on the UI thread.
/// Actions whose feature is not built yet say so; each is replaced in the milestone named in its message.
/// </summary>
public sealed class AppCommands(
    IServiceProvider services, ScreenshotService screenshots, ForegroundWindowTracker foregroundTracker,
    UserNotifications notifications, ILogger<AppCommands> logger)
{
    private LibraryWindow? _library;

    public void Execute(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.CaptureRegion:
                CaptureRegion();
                break;
            case HotkeyAction.CaptureFullScreen:
                CaptureFullScreen();
                break;
            case HotkeyAction.CaptureActiveWindow:
                CaptureActiveWindow();
                break;
            case HotkeyAction.ScrollingCapture:
                ScrollingCapture();
                break;
            case HotkeyAction.CaptureText:
                CaptureText();
                break;
            case HotkeyAction.ToggleRecording:
                ToggleRecording();
                break;
            case HotkeyAction.PauseRecording:
                PauseRecording();
                break;
            default:
                logger.LogWarning("No command for hotkey action {Action}", action);
                break;
        }
    }

    public void CaptureRegion() => _ = screenshots.CaptureAsync(CaptureKind.Region);

    /// <summary>
    /// The Library's own Capture button: hide the Library first so it is not in the screenshot, and bring it back
    /// afterwards.
    /// </summary>
    public async Task CaptureFromLibraryAsync(CaptureKind kind)
    {
        var library = _library;
        if (library is null || !library.IsVisible || screenshots.IsBusy)
        {
            await screenshots.CaptureAsync(kind);
            return;
        }

        library.Hide();
        try
        {
            await Task.Delay(WindowHideDelay); // let the desktop repaint without it
            await screenshots.CaptureAsync(kind);
        }
        finally
        {
            if (_library == library)
            {
                library.Show();
            }
        }
    }

    /// <summary>How long a menu or window needs to disappear from the screen before a capture freezes it.</summary>
    public static readonly TimeSpan WindowHideDelay = TimeSpan.FromMilliseconds(250);

    public void CaptureFullScreen() => _ = screenshots.CaptureAsync(CaptureKind.FullScreen);

    public void CaptureActiveWindow() => _ = screenshots.CaptureAsync(CaptureKind.Window);

    /// <summary>"Capture window" from the tray menu: the window the user was in before opening the menu.</summary>
    public void CaptureLastUserWindow() =>
        _ = screenshots.CaptureAsync(CaptureKind.Window, foregroundTracker.LastUserWindow is var w and not 0 ? w : null);

    public void ScrollingCapture() => NotYet("Scrolling capture", "M5");

    public void CaptureText() => NotYet("Text capture", "M5");

    public void ToggleRecording() => NotYet("Screen recording", "M7");

    public void PauseRecording() => NotYet("Pause recording", "M7");

    public void OpenSettings() => NotYet("Settings", "M4");

    public void CheckForUpdates() => NotYet("Updates", "M10");

    public void OpenLibrary()
    {
        if (_library is null)
        {
            _library = services.GetRequiredService<LibraryWindow>();
            _library.Closed += (_, _) => _library = null;
            _library.Show();
        }

        if (_library.WindowState == WindowState.Minimized)
        {
            _library.WindowState = WindowState.Normal;
        }

        _library.Activate();
    }

    public void Exit()
    {
        logger.LogInformation("Exit requested");

        // Ask about unsaved editors first: during Shutdown, WPF ignores a window's request to stay open.
        if (!services.GetRequiredService<Editor.EditorService>().ConfirmCloseAll())
        {
            logger.LogInformation("Exit cancelled to keep unsaved changes");
            return;
        }

        Application.Current.Shutdown();
    }

    private void NotYet(string feature, string milestone)
    {
        logger.LogInformation("{Feature} requested; not built yet (milestone {Milestone})", feature, milestone);
        notifications.Show($"{feature} is coming soon.");
    }
}
