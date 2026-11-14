using Grably.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Display;

/// <summary>Pointer position and window placement in physical pixels, independent of any WPF DPI scaling.</summary>
public static class ScreenPointer
{
    public static PixelPoint Position() =>
        PInvoke.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;

    /// <summary>Places a window exactly over <paramref name="bounds"/> (physical pixels) and makes it topmost.</summary>
    public static bool PlaceTopmost(nint hwnd, PixelRect bounds) =>
        PInvoke.SetWindowPos(new HWND(hwnd), HWND.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
}
