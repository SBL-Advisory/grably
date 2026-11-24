using Grably.Core.Capture;
using Grably.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Display;

/// <summary>Monitors and the virtual desktop, in physical pixels (the process must be per-monitor DPI aware).</summary>
public static class MonitorService
{
    /// <summary>The bounding box of all monitors.</summary>
    public static PixelRect VirtualScreen => new(
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));

    /// <summary>All monitors ordered left to right, then top to bottom; Index follows that order.</summary>
    public static unsafe IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var found = new List<MonitorInfo>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (monitor, _, _, _) =>
        {
            var info = default(MONITORINFOEXW);
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                var dpi = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Succeeded
                    ? (int)dpiX
                    : 96;
                found.Add(new MonitorInfo(
                    0,
                    info.szDevice.ToString(),
                    ToPixelRect(info.monitorInfo.rcMonitor),
                    ToPixelRect(info.monitorInfo.rcWork),
                    dpi,
                    (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }, default);

        return found
            .OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)
            .Select((m, i) => m with { Index = i })
            .ToList();
    }

    /// <summary>The HMONITOR of the monitor showing <paramref name="point"/> (or the nearest one).</summary>
    public static unsafe nint HandleAt(PixelPoint point) =>
        (nint)PInvoke.MonitorFromPoint(new System.Drawing.Point(point.X, point.Y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST).Value;

    internal static PixelRect ToPixelRect(RECT r) => PixelRect.FromEdges(r.left, r.top, r.right, r.bottom);
}
