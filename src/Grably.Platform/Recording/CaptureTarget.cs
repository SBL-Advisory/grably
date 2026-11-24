using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Platform.Display;

namespace Grably.Platform.Recording;

public enum CaptureTargetKind
{
    Monitor,
    Window,
}

/// <summary>
/// What a recording captures: a monitor, a window, or a region (a monitor with a crop; a region is limited to one
/// monitor). <paramref name="Crop"/> is in the captured content's own pixels.
/// </summary>
/// <param name="Description">For people ("Window: Quarterly report.docx"); kept with the recording, never logged.</param>
/// <param name="LogDescription">For the log: no window titles, which can hold document names or email subjects.</param>
/// <param name="ScreenArea">What is recorded on screen (physical pixels); for a window, where it was at the start.</param>
public sealed record CaptureTarget(
    CaptureTargetKind Kind, nint Handle, PixelRect? Crop, string Description, string LogDescription, PixelRect ScreenArea)
{
    public static CaptureTarget ForMonitor(MonitorInfo monitor) => new(
        CaptureTargetKind.Monitor, MonitorService.HandleAt(monitor.Bounds.Center), null, $"Monitor {monitor.Index + 1}",
        $"monitor {monitor.Index + 1}", monitor.Bounds);

    public static CaptureTarget ForWindow(WindowInfo window) => new(
        CaptureTargetKind.Window, window.Handle, null, $"Window: {window.Title}", $"a window of {window.ProcessName}", window.Bounds);

    /// <summary>What is recorded on screen now: a window's current frame (one cheap DWM call), else the fixed area.</summary>
    public PixelRect CurrentArea() =>
        Kind == CaptureTargetKind.Window && WindowActions.FrameBounds(Handle) is { IsEmpty: false } frame ? frame : ScreenArea;

    /// <summary>The part of <paramref name="region"/> on the monitor under its centre.</summary>
    /// <exception cref="ArgumentException">The region is not on any monitor.</exception>
    public static CaptureTarget ForRegion(PixelRect region, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(region.Center))
            ?? throw new ArgumentException($"The region {region} is not on a monitor.", nameof(region));
        var onMonitor = region.Intersect(monitor.Bounds);
        return new CaptureTarget(
            CaptureTargetKind.Monitor,
            MonitorService.HandleAt(monitor.Bounds.Center),
            onMonitor.Offset(-monitor.Bounds.X, -monitor.Bounds.Y),
            $"Region {onMonitor.Width}x{onMonitor.Height} at {onMonitor.X},{onMonitor.Y}",
            $"a {onMonitor.Width}x{onMonitor.Height} region",
            onMonitor);
    }
}
