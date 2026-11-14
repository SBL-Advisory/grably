using Grably.Core.Geometry;

namespace Grably.Core.Capture;

/// <summary>Finds which window a point is over, for the overlay's hover-to-snap.</summary>
public static class WindowHitTest
{
    /// <summary>
    /// The first window in <paramref name="windowsTopToBottom"/> (z-order, topmost first) whose bounds contain
    /// <paramref name="point"/>, or null.
    /// </summary>
    public static WindowInfo? WindowAt(IEnumerable<WindowInfo> windowsTopToBottom, PixelPoint point) =>
        windowsTopToBottom.FirstOrDefault(w => w.Bounds.Contains(point));

    /// <summary>The part of a window that can actually be captured: its bounds clipped to the desktop.</summary>
    public static PixelRect VisibleBounds(WindowInfo window, PixelRect desktop) => window.Bounds.Intersect(desktop);
}
