using Grably.Core.Geometry;
using Grably.Platform.Capture;
using Grably.Platform.Display;

namespace Grably.Tests.Platform;

/// <summary>Needs a real, unlocked desktop session.</summary>
[Trait("Category", "Desktop")]
public class ScreenCaptureTests
{
    static ScreenCaptureTests() => DpiAwareness.EnsurePerMonitorV2();

    [Fact]
    public void Monitors_cover_the_virtual_desktop_and_one_is_primary_at_the_origin()
    {
        var monitors = MonitorService.GetMonitors();
        var union = monitors.Aggregate(PixelRect.Empty, (acc, m) => acc.Union(m.Bounds));

        Assert.NotEmpty(monitors);
        Assert.Equal(MonitorService.VirtualScreen, union);
        var primary = Assert.Single(monitors, m => m.IsPrimary);
        Assert.Equal(new PixelPoint(0, 0), primary.Bounds.Location);
        Assert.All(monitors, m => Assert.InRange(m.Dpi, 96, 480));
        Assert.Equal(Enumerable.Range(0, monitors.Count), monitors.Select(m => m.Index));
    }

    [Fact]
    public void Grab_of_each_monitor_has_its_exact_pixel_size_and_is_opaque()
    {
        foreach (var monitor in MonitorService.GetMonitors())
        {
            var image = ScreenGrabber.Grab(monitor.Bounds);

            Assert.Equal(monitor.Bounds.Size, image.Size);
            Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(255, image.Pixels[(i * 4) + 3]));
        }
    }

    [Fact]
    public void Grab_of_the_virtual_desktop_has_its_size()
    {
        var desktop = MonitorService.VirtualScreen;

        var image = ScreenGrabber.Grab(desktop, includeCursor: true);

        Assert.Equal(desktop.Size, image.Size);
    }

    [Fact]
    public void Visible_windows_have_bounds_skip_the_desktop_and_can_exclude_a_process()
    {
        var all = WindowEnumerator.GetVisibleWindows();
        Assert.NotEmpty(all); // at least the taskbar
        Assert.All(all, w => Assert.False(w.Bounds.IsEmpty));
        Assert.DoesNotContain(all, w => w.ClassName is "Progman" or "WorkerW");

        var excluded = all[0].ProcessId;
        var remaining = WindowEnumerator.GetVisibleWindows(excluded);

        Assert.DoesNotContain(remaining, w => w.ProcessId == excluded);
        Assert.True(remaining.Count < all.Count);
    }
}
