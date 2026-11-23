using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Display;

public static class WindowActions
{
    /// <summary>Asks a window to close, exactly as Alt+F4 or its close button would (posts WM_CLOSE).</summary>
    public static bool RequestClose(nint hwnd) =>
        PInvoke.PostMessage(new HWND(hwnd), PInvoke.WM_CLOSE, default, default);

    /// <summary>The window in front (it has the keyboard).</summary>
    public static nint Foreground() => PInvoke.GetForegroundWindow();

    /// <summary>
    /// A window's visible frame in physical pixels (DWM's extended frame bounds, without the invisible resize border),
    /// with one cheap call; null if it is gone.
    /// </summary>
    public static unsafe Grably.Core.Geometry.PixelRect? FrameBounds(nint hwnd)
    {
        RECT r;
        return hwnd != 0 && PInvoke.DwmGetWindowAttribute(new HWND((void*)hwnd), Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)).Succeeded
            ? Grably.Core.Geometry.PixelRect.FromEdges(r.left, r.top, r.right, r.bottom)
            : null;
    }

    /// <summary>A window's rectangle on screen in physical pixels (GetWindowRect), or null if it is gone.</summary>
    public static unsafe Grably.Core.Geometry.PixelRect? Bounds(nint hwnd) =>
        PInvoke.GetWindowRect(new HWND((void*)hwnd), out var r) ? Grably.Core.Geometry.PixelRect.FromEdges(r.left, r.top, r.right, r.bottom) : null;

    /// <summary>
    /// Leaves the window out of every screen capture (it still shows on the monitor), so Grably's own status
    /// windows never end up in a screenshot or recording. Returns false on Windows versions without support.
    /// </summary>
    public static bool ExcludeFromCapture(nint hwnd) =>
        PInvoke.SetWindowDisplayAffinity(new HWND(hwnd), WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
}
