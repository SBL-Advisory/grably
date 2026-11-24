using Grably.Core.Recording;

namespace Grably.Tests.Core;

public class RecordingClockTests
{
    private long _now = 1_000_000;

    private RecordingClock Clock() => new(() => _now);

    private void Advance(double seconds) => _now += (long)(seconds * TimeSpan.TicksPerSecond);

    [Fact]
    public void Pauses_freeze_the_time_and_are_left_out_of_it()
    {
        var clock = Clock();
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        clock.Start();
        Advance(2);
        clock.Pause();
        Advance(5);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Elapsed);
        clock.Resume();
        Advance(1);

        Assert.Equal(TimeSpan.FromSeconds(3), clock.Elapsed);
        Assert.False(clock.IsPaused);
    }

    [Fact]
    public void Moments_map_onto_the_timeline_and_those_in_a_pause_or_before_the_start_do_not()
    {
        var clock = Clock();
        var beforeStart = _now - 10;
        clock.Start();
        var start = _now;
        Advance(2);
        clock.Pause();
        var pauseStart = _now;
        Advance(5);
        clock.Resume();
        var resumed = _now;
        Advance(1);

        Assert.Null(clock.At(beforeStart));
        Assert.Equal(TimeSpan.FromSeconds(1), clock.At(start + TimeSpan.TicksPerSecond));
        Assert.Null(clock.At(pauseStart + 10)); // audio captured while paused is dropped
        Assert.Equal(TimeSpan.FromSeconds(2.5), clock.At(resumed + (TimeSpan.TicksPerSecond / 2)));
    }

    [Fact]
    public void The_performance_counter_is_in_100_ns_ticks()
    {
        var a = RecordingClock.PerformanceCounterNow();
        Thread.Sleep(50);
        var elapsed = TimeSpan.FromTicks(RecordingClock.PerformanceCounterNow() - a);

        Assert.InRange(elapsed.TotalMilliseconds, 40, 500);
    }

    [Fact]
    public void Gaps_are_filled_with_silence_and_overlaps_dropped_but_jitter_is_left_alone()
    {
        var timeline = new AudioTimeline(48_000);
        var tolerance = TimeSpan.FromMilliseconds(20);

        Assert.Equal((0, 0), timeline.Place(TimeSpan.Zero, 480, tolerance)); // 10 ms
        Assert.Equal((0, 0), timeline.Place(TimeSpan.FromMilliseconds(15), 480, tolerance)); // 5 ms late: jitter

        // Loopback said nothing for a second (the PC was silent): fill it.
        var (pad, skip) = timeline.Place(TimeSpan.FromSeconds(1), 480, tolerance);
        Assert.Equal((48_000 - 960, 0), (pad, skip));
        Assert.Equal(48_480, timeline.FramesWritten);

        // The device clock ran 30 ms fast: drop the overlap.
        (pad, skip) = timeline.Place(TimeSpan.FromMilliseconds(980), 4_800, tolerance);
        Assert.Equal((0, 48_480 - 47_040), (pad, skip));
        Assert.Equal(47_040 + 4_800, timeline.FramesWritten);
    }
}
