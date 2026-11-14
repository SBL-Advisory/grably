using Windows.Win32;
using Windows.Win32.Foundation;

namespace Grably.Platform.Display;

public static class WindowActions
{
    /// <summary>Asks a window to close, exactly as Alt+F4 or its close button would (posts WM_CLOSE).</summary>
    public static bool RequestClose(nint hwnd) =>
        PInvoke.PostMessage(new HWND(hwnd), PInvoke.WM_CLOSE, default, default);
}
