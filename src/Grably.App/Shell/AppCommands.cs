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
/// </summary>
public sealed class AppCommands(
    IServiceProvider services, ScreenshotService screenshots, ForegroundWindowTracker foregroundTracker, ILogger<AppCommands> logger)
{
    private LibraryWindow? _library;
    private SettingsWindow? _settings;

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

    public void ScrollingCapture() => _ = services.GetRequiredService<ScrollingCaptureService>().CaptureAsync();

    public void CaptureText() => _ = services.GetRequiredService<Text.TextCaptureService>().CaptureAsync();

    public void ToggleRecording() => _ = services.GetRequiredService<Recording.RecordingController>().ToggleAsync();

    public void PauseRecording() => services.GetRequiredService<Recording.RecordingController>().TogglePause();

    public void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = services.GetRequiredService<SettingsWindow>();
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }

        if (_settings.WindowState == WindowState.Minimized)
        {
            _settings.WindowState = WindowState.Normal;
        }

        _settings.Activate();
    }

    public void CheckForUpdates() => _ = services.GetRequiredService<UpdateService>().CheckAsync(userAsked: true);

    /// <summary>Quits like Exit (with the same questions), then the downloaded update installs and Grably starts again.</summary>
    public void RestartToUpdate() => _ = ExitAsync(update: true);

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

    public void Exit() => _ = ExitAsync(update: false);

    private async Task ExitAsync(bool update)
    {
        logger.LogInformation(update ? "Restart to update requested" : "Exit requested");
        var updates = services.GetRequiredService<UpdateService>();
        if (update && !updates.CanInstall)
        {
            logger.LogWarning("No update can be installed now; not restarting");
            return;
        }

        if (!await services.GetRequiredService<Recording.RecordingController>().ConfirmExitAsync())
        {
            logger.LogInformation("Exit cancelled to keep the recording going");
            return;
        }

        if (!await services.GetRequiredService<Trim.TrimService>().ConfirmExitAsync())
        {
            logger.LogInformation("Exit cancelled to finish an export");
            return;
        }

        // Ask about unsaved editors first: during Shutdown, WPF ignores a window's request to stay open.
        if (!services.GetRequiredService<Editor.EditorService>().ConfirmCloseAll())
        {
            logger.LogInformation("Exit cancelled to keep unsaved changes");
            return;
        }

        // Quit even if the updater could not be started: the recording is already saved, and the downloaded update
        // installs the next time Grably starts.
        if (update && !updates.InstallOnExit())
        {
            logger.LogWarning("Quitting without restarting; the update installs at the next start");
        }

        Application.Current.Shutdown();
    }
}
