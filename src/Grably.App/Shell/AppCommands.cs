using System.Windows;
using Grably.App.Views;
using Grably.Core.Hotkeys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grably.App.Shell;

/// <summary>
/// Every user-facing action, whether it comes from a hotkey, the tray menu or a window. Call on the UI thread.
/// Actions whose feature is not built yet log and say so; each is replaced in the milestone named in the message.
/// </summary>
public sealed class AppCommands(IServiceProvider services, ILogger<AppCommands> logger)
{
    private LibraryWindow? _library;

    /// <summary>Raised when an action is not available yet, so the shell can tell the user.</summary>
    public event EventHandler<string>? NotAvailable;

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

    public void CaptureRegion() => NotYet("Region capture", "M2");

    public void CaptureFullScreen() => NotYet("Full-screen capture", "M2");

    public void CaptureActiveWindow() => NotYet("Window capture", "M2");

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
        Application.Current.Shutdown();
    }

    private void NotYet(string feature, string milestone)
    {
        logger.LogInformation("{Feature} requested; not built yet (milestone {Milestone})", feature, milestone);
        NotAvailable?.Invoke(this, $"{feature} is coming soon.");
    }
}
