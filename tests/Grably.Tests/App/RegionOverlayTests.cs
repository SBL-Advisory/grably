using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Grably.App.Capture.Overlay;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Imaging;
using Grably.Platform.Capture;
using Grably.Platform.Display;

namespace Grably.Tests.App;

/// <summary>
/// Shows real overlay windows (briefly, in the top-left corner of the screen), so it needs a desktop session.
/// Covers the lifetime rules: however the overlay ends, every overlay window closes and the capture completes.
/// </summary>
[Trait("Category", "Desktop")]
public class RegionOverlayTests
{
    private static readonly PixelRect Desktop = new(0, 0, 400, 200);
    private static readonly MonitorInfo[] Monitors =
    [
        new(0, "A", new PixelRect(0, 0, 200, 200), new PixelRect(0, 0, 200, 200), 96, true),
        new(1, "B", new PixelRect(200, 0, 200, 200), new PixelRect(200, 0, 200, 200), 96, false),
    ];

    static RegionOverlayTests() => DpiAwareness.EnsurePerMonitorV2();

    [Fact]
    public void Closing_one_overlay_like_Alt_F4_cancels_and_closes_all_of_them() => RunSta(() =>
    {
        var selection = new RegionSelection(Desktop, Monitors, [], adjustBeforeCapture: false);
        var task = RegionOverlay.RunAsync(selection, new PixelBuffer(400, 200), Monitors);
        Pump(TimeSpan.FromMilliseconds(300));

        var overlays = OverlayWindows();
        Assert.Equal(2, overlays.Count);
        WindowActions.RequestClose(overlays[0]);

        Assert.True(PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)), "the capture never finished");
        Assert.Null(task.Result);
        Assert.Equal(SelectionPhase.Done, selection.Phase);
        Assert.True(PumpUntil(() => OverlayWindows().Count == 0, TimeSpan.FromSeconds(2)), "an overlay stayed on screen");
    });

    [Fact]
    public void Completing_the_selection_closes_every_overlay_and_returns_the_area() => RunSta(() =>
    {
        var selection = new RegionSelection(Desktop, Monitors, [], adjustBeforeCapture: false);
        var task = RegionOverlay.RunAsync(selection, new PixelBuffer(400, 200), Monitors);
        Pump(TimeSpan.FromMilliseconds(200));

        selection.Press(new PixelPoint(150, 50));
        selection.Release(new PixelPoint(249, 99)); // across both "monitors"

        Assert.True(PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)));
        Assert.Equal(new PixelRect(150, 50, 100, 50), task.Result);
        Assert.True(PumpUntil(() => OverlayWindows().Count == 0, TimeSpan.FromSeconds(2)));
    });

    private static List<nint> OverlayWindows() => WindowEnumerator.GetVisibleWindows()
        .Where(w => w.ProcessId == Environment.ProcessId && w.Title == "Grably capture")
        .Select(w => w.Handle)
        .ToList();

    private static void Pump(TimeSpan duration) => PumpUntil(() => false, duration);

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }

        return condition();
    }

    private static void RunSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
