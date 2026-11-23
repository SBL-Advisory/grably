using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Display;

public static class WindowActions
{
    /// <summary>Asks a window to close, exactly as Alt+F4 or its close button would (posts WM_CLOSE).</summary>
    public static bool RequestClose(nint hwnd) =>
        PInvoke.PostMessage(new HWND(hwnd), PInvoke.WM_CLOSE, default, default);

    /// <summary>
    /// Leaves the window out of every screen capture (it still shows on the monitor), so Grably's own status
    /// windows never end up in a screenshot or recording. Returns false on Windows versions without support.
    /// </summary>
    public static bool ExcludeFromCapture(nint hwnd) =>
        PInvoke.SetWindowDisplayAffinity(new HWND(hwnd), WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
}
