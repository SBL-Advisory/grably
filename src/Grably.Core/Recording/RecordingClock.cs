using System.Diagnostics;

namespace Grably.Core.Recording;

/// <summary>
/// The time a recording has been running, not counting pauses. It reads the performance counter, the clock Windows
/// stamps audio packets with, so a packet's timestamp maps straight onto the recording's timeline and audio lines up
/// with the frames. Times are in 100-nanosecond ticks. Thread-safe.
/// </summary>
public sealed class RecordingClock
{
    private readonly Func<long> _now;
    private readonly object _lock = new();
    private readonly List<(long Start, long End)> _pauses = [];
    private long _start = -1;
    private long? _pausedAt;

    /// <param name="now">The current time in 100-ns ticks; the performance counter by default (tests pass their own).</param>
    public RecordingClock(Func<long>? now = null) => _now = now ?? PerformanceCounterNow;

    public bool IsStarted
    {
        get
        {
            lock (_lock)
            {
                return _start >= 0;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _pausedAt is not null;
            }
        }
    }

    /// <summary>Recording time so far (frozen while paused; zero before the start).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
            {
                return _start < 0 ? TimeSpan.Zero : TimeSpan.FromTicks((_pausedAt ?? _now()) - _start - PausedBefore(long.MaxValue));
            }
        }
    }

    /// <summary>The performance counter in 100-ns ticks, the unit of WASAPI's packet timestamps.</summary>
    public static long PerformanceCounterNow()
    {
        var ticks = Stopwatch.GetTimestamp();
        var frequency = Stopwatch.Frequency;
        return ((ticks / frequency) * TimeSpan.TicksPerSecond) + ((ticks % frequency) * TimeSpan.TicksPerSecond / frequency);
    }

    /// <summary>The current time on this clock's counter, in 100-ns ticks.</summary>
    public long Now() => _now();

    public void Start()
    {
        lock (_lock)
        {
            _start = _now();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_start >= 0 && _pausedAt is null)
            {
                _pausedAt = _now();
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_pausedAt is { } since)
            {
                _pauses.Add((since, _now()));
                _pausedAt = null;
            }
        }
    }

    /// <summary>
    /// Where a moment (in 100-ns performance-counter ticks) falls on the recording's timeline: null before the start
    /// or during a pause, when whatever happened then is not part of the recording.
    /// </summary>
    public TimeSpan? At(long ticks)
    {
        lock (_lock)
        {
            if (_start < 0 || ticks < _start || (_pausedAt is { } since && ticks >= since))
            {
                return null;
            }

            foreach (var (start, end) in _pauses)
            {
                if (ticks >= start && ticks < end)
                {
                    return null;
                }
            }

            return TimeSpan.FromTicks(ticks - _start - PausedBefore(ticks));
        }
    }

    private long PausedBefore(long ticks)
    {
        long total = 0;
        foreach (var (start, end) in _pauses)
        {
            if (end <= ticks)
            {
                total += end - start;
            }
        }

        return total;
    }
}

/// <summary>
/// Keeps one audio track on the recording clock. Each packet carries the time it was captured; when the track has
/// fallen behind that time (loopback sends nothing while the PC is silent, a device was switched), silence fills the
/// gap; when it has run ahead (device clocks drift), the overlap is dropped. Within the tolerance nothing is changed,
/// so normal jitter never causes clicks.
/// </summary>
public sealed class AudioTimeline(int sampleRate)
{
    public int SampleRate { get; } = sampleRate > 0 ? sampleRate : throw new ArgumentOutOfRangeException(nameof(sampleRate));

    /// <summary>Sample frames written to the track so far (silence included).</summary>
    public long FramesWritten { get; private set; }

    /// <summary>How many silent frames to write before a packet, and how many of its frames to skip.</summary>
    /// <param name="start">Recording time of the packet's first frame.</param>
    public (long PadFrames, int SkipFrames) Place(TimeSpan start, int frames, TimeSpan tolerance)
    {
        var expected = (long)Math.Round(start.TotalSeconds * SampleRate);
        var behind = expected - FramesWritten;
        var limit = (long)(tolerance.TotalSeconds * SampleRate);
        long pad = 0;
        var skip = 0;
        if (behind > limit)
        {
            pad = behind;
        }
        else if (behind < -limit)
        {
            skip = (int)Math.Min(frames, -behind);
        }

        FramesWritten += pad + frames - skip;
        return (pad, skip);
    }
}
