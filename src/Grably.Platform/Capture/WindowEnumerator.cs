using System.Diagnostics;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Platform.Display;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Capture;

/// <summary>Lists the top-level windows a user can see, in z-order, with their visible frames in physical pixels.</summary>
public static class WindowEnumerator
{
    // The desktop itself would match everywhere; it is selected through the monitor fallback instead.
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW" };

    /// <summary>Visible, non-minimised, non-cloaked windows, topmost first.</summary>
    /// <param name="excludeProcessId">Skip this process's windows (Grably's own overlays); null keeps all.</param>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows(int? excludeProcessId = null)
    {
        var windows = new List<WindowInfo>();
        var processNames = new Dictionary<uint, string>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            var info = Describe(hwnd, excludeProcessId, processNames);
            if (info is not null)
            {
                windows.Add(info);
            }

            return true;
        }, default);
        return windows;
    }

    /// <summary>The window that has the keyboard focus, or null if it cannot be captured.</summary>
    public static WindowInfo? GetForegroundWindow(int? excludeProcessId = null) =>
        Describe(PInvoke.GetForegroundWindow(), excludeProcessId, []);

    /// <summary>A specific window as it is now, or null if it is gone, hidden or minimised.</summary>
    public static unsafe WindowInfo? GetWindow(nint handle) =>
        handle == 0 ? null : Describe(new HWND((void*)handle), null, []);

    private static unsafe WindowInfo? Describe(HWND hwnd, int? excludeProcessId, Dictionary<uint, string> processNames)
    {
        if (hwnd.IsNull || !PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
        {
            return null;
        }

        // Cloaked windows are "visible" but not on screen (other virtual desktops, suspended UWP apps).
        int cloaked;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int)).Succeeded && cloaked != 0)
        {
            return null;
        }

        // Click-through windows (full-screen overlays from GPU tools etc.) can never be what the user points at.
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TRANSPARENT))
        {
            return null;
        }

        var className = ClassName(hwnd);
        if (IgnoredClasses.Contains(className))
        {
            return null;
        }

        uint processId;
        _ = PInvoke.GetWindowThreadProcessId(hwnd, &processId);
        if (excludeProcessId is { } exclude && processId == (uint)exclude)
        {
            return null;
        }

        var bounds = VisibleBounds(hwnd);
        if (bounds.IsEmpty)
        {
            return null;
        }

        return new WindowInfo((nint)hwnd.Value, Title(hwnd), (int)processId, ProcessName(processId, processNames), className, bounds);
    }

    /// <summary>The frame as drawn (DWM extended frame bounds), without the invisible resize border.</summary>
    private static unsafe PixelRect VisibleBounds(HWND hwnd)
    {
        RECT rect;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT)).Succeeded)
        {
            return MonitorService.ToPixelRect(rect);
        }

        return PInvoke.GetWindowRect(hwnd, out rect) ? MonitorService.ToPixelRect(rect) : PixelRect.Empty;
    }

    private static unsafe string Title(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var copied = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return new string(buffer, 0, copied);
        }
    }

    private static unsafe string ClassName(HWND hwnd)
    {
        var buffer = stackalloc char[256];
        var copied = PInvoke.GetClassName(hwnd, buffer, 256);
        return new string(buffer, 0, copied);
    }

    private static string ProcessName(uint processId, Dictionary<uint, string> cache)
    {
        if (cache.TryGetValue(processId, out var name))
        {
            return name;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            name = string.Empty; // exited meanwhile, or access denied
        }

        cache[processId] = name;
        return name;
    }
}
