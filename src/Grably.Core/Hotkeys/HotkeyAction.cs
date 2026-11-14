namespace Grably.Core.Hotkeys;

/// <summary>Everything a global hotkey can trigger.</summary>
public enum HotkeyAction
{
    CaptureRegion,
    CaptureFullScreen,
    CaptureActiveWindow,
    ScrollingCapture,
    CaptureText,
    ToggleRecording,

    /// <summary>Only registered while a recording is running, so it does not block the key combination otherwise.</summary>
    PauseRecording,
}
