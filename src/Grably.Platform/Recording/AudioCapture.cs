using Grably.Core.Recording;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Grably.Platform.Recording;

public enum AudioTrackKind
{
    /// <summary>What the PC plays (WASAPI loopback of the output device).</summary>
    System,

    Microphone,

    /// <summary>A generated tone, for the dev harness's sync test.</summary>
    TestTone,
}

/// <summary>Every track is captured as 48 kHz stereo 32-bit float (Windows converts), so tracks mix without resampling.</summary>
public static class AudioTrackFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BytesPerFrame = Channels * sizeof(float);

    /// <summary>ffmpeg input options describing a track file.</summary>
    public static IReadOnlyList<string> FfmpegInput => ["-f", "f32le", "-ar", "48000", "-ac", "2"];

    internal static WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
}

/// <summary>
/// One audio track of a recording, written as raw samples to its own file on the recording timeline: packets land
/// where their capture time says (silence fills gaps, overlaps are dropped), packets captured during a pause are
/// left out, and a muted track writes silence. Raw samples rather than WAV: nothing to finalise, so a crash loses
/// nothing written, and no 4 GB limit.
/// </summary>
internal sealed class AudioTrackWriter : IDisposable
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(20);
    private static readonly byte[] Zeros = new byte[64 * 1024];

    private readonly RecordingClock _clock;
    private readonly FileStream _file;
    private readonly AudioTimeline _timeline = new(AudioTrackFormat.SampleRate);
    private readonly object _lock = new();
    private bool _closed;

    public AudioTrackWriter(AudioTrackKind kind, string folder, RecordingClock clock)
    {
        Kind = kind;
        _clock = clock;
        FileName = $"{kind.ToString().ToLowerInvariant()}.f32";
        _file = new FileStream(Path.Combine(folder, FileName), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
    }

    public AudioTrackKind Kind { get; }

    /// <summary>The track file's name inside the session folder.</summary>
    public string FileName { get; }

    /// <summary>Write silence instead of what is captured (the microphone's mute button).</summary>
    public bool Muted { get; set; }

    /// <summary>Takes one captured packet. Safe to call from the capture thread.</summary>
    /// <param name="capturedAt">The packet's capture time in 100-ns performance-counter ticks.</param>
    public void Write(ReadOnlySpan<byte> data, bool silent, long capturedAt)
    {
        var frames = data.Length / AudioTrackFormat.BytesPerFrame;

        // Some (virtual) devices report no timestamp or a wild one: then the packet ends now.
        var now = _clock.Now();
        if (capturedAt <= 0 || Math.Abs(capturedAt - now) > 5 * TimeSpan.TicksPerSecond)
        {
            capturedAt = now - ((long)frames * TimeSpan.TicksPerSecond / AudioTrackFormat.SampleRate);
        }

        if (_clock.At(capturedAt) is not { } at)
        {
            return; // before the start or during a pause
        }

        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            var (pad, skip) = _timeline.Place(at, frames, Tolerance);
            WriteSilence(pad * AudioTrackFormat.BytesPerFrame);
            var kept = data[(skip * AudioTrackFormat.BytesPerFrame)..(frames * AudioTrackFormat.BytesPerFrame)];
            if (silent || Muted)
            {
                WriteSilence(kept.Length);
            }
            else
            {
                _file.Write(kept);
            }
        }
    }

    /// <summary>
    /// Fills a gap with silence up to <paramref name="time"/> as it happens (loopback sends nothing while the PC is
    /// silent), so a long quiet stretch is never written in one go when sound returns.
    /// </summary>
    public void PadTo(TimeSpan time)
    {
        lock (_lock)
        {
            if (_closed || time <= TimeSpan.Zero)
            {
                return;
            }

            var behind = (long)(time.TotalSeconds * AudioTrackFormat.SampleRate) - _timeline.FramesWritten;
            if (behind > 0)
            {
                var (pad, _) = _timeline.Place(time, 0, TimeSpan.Zero);
                WriteSilence(pad * AudioTrackFormat.BytesPerFrame);
            }
        }
    }

    /// <summary>
    /// Fills the track with silence up to <paramref name="end"/> and closes it. Loopback sends nothing while the PC is
    /// silent, so without this a quiet recording would have an empty (and so missing) sound track.
    /// </summary>
    public void Finish(TimeSpan end)
    {
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            var (pad, _) = _timeline.Place(end, 0, TimeSpan.Zero);
            WriteSilence(pad * AudioTrackFormat.BytesPerFrame);
        }

        Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_closed)
            {
                _closed = true;
                _file.Dispose();
            }
        }
    }

    private void WriteSilence(long bytes)
    {
        while (bytes > 0)
        {
            var chunk = (int)Math.Min(bytes, Zeros.Length);
            _file.Write(Zeros, 0, chunk);
            bytes -= chunk;
        }
    }
}

internal interface IAudioSource : IDisposable
{
    void Start();
}

/// <summary>
/// WASAPI capture of the system output (loopback) or a microphone into a track. The microphone follows the default
/// device when none is chosen (Windows reroutes the stream); loopback is reopened when the default output device
/// changes, checked once a second.
/// </summary>
internal sealed class WasapiAudioSource(AudioTrackKind kind, string? deviceId, AudioTrackWriter writer, ILogger logger, Action<string> warn) : IAudioSource
{
    private bool _warned;

    private readonly object _lock = new();
    private WasapiRecorder? _recorder;
    private Timer? _deviceWatch;
    private string? _currentOutput;
    private bool _disposed;

    /// <summary>The device being captured, for the log.</summary>
    public string? DeviceName { get; private set; }

    public void Start()
    {
        Open();
        if (kind == AudioTrackKind.System && deviceId is null)
        {
            _deviceWatch = new Timer(_ => FollowDefaultOutput(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    public void Dispose()
    {
        _deviceWatch?.Dispose();
        lock (_lock)
        {
            _disposed = true;
            Close();
        }
    }

    private void Open()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            using var devices = new MMDeviceEnumerator();
            var builder = new WasapiRecorderBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithFormat(AudioTrackFormat.WaveFormat)
                .WithMmcssThreadPriority("Audio");
            string? output = null;
            if (kind == AudioTrackKind.System)
            {
                var device = deviceId is null ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : devices.GetDevice(deviceId);
                output = device.ID;
                builder = builder.WithLoopbackCapture().WithDevice(device);
            }
            else
            {
                builder = deviceId is null ? builder.WithDefaultDeviceStreamRouting() : builder.WithDevice(devices.GetDevice(deviceId));
            }

            _recorder = builder.Build();
            DeviceName = _recorder.DeviceFriendlyName;
            _recorder.DataAvailable += OnData;
            _recorder.RecordingStopped += (_, e) =>
            {
                if (e.Exception is { } error)
                {
                    logger.LogWarning(error, "{Track} audio capture stopped", kind);
                }
            };
            _recorder.StartRecording();
            _currentOutput = output; // only once it records: a failed switch is tried again a second later
        }
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition) =>
        writer.Write(buffer, (flags & AudioClientBufferFlags.Silent) != 0, qpcPosition);

    private void Close()
    {
        if (_recorder is { } recorder)
        {
            _recorder = null;
            recorder.DataAvailable -= OnData;
            try
            {
                recorder.StopRecording();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Already stopped (device removed).
            }

            recorder.Dispose();
        }
    }

    private void FollowDefaultOutput()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var current = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            if (current == _currentOutput)
            {
                return;
            }

            logger.LogInformation("The default output device changed; following it");
            lock (_lock)
            {
                Close();
            }

            Open(); // the gap while switching becomes silence on the timeline
            _warned = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // This runs on a timer thread: an exception escaping it would end the app mid-recording.
            logger.LogWarning(ex, "Following the default output device failed; trying again");
            if (!_warned)
            {
                _warned = true;
                warn("The PC's sound is not being recorded since the output device changed. Grably keeps trying.");
            }
        }
    }
}

/// <summary>
/// A generated track for the sync test: silence with a 1 kHz beep of 150 ms at each of the given recording times,
/// stamped with the performance counter exactly like WASAPI packets, so it travels the same path as real audio.
/// </summary>
internal sealed class TestToneSource(AudioTrackWriter writer, RecordingClock clock, IReadOnlyList<TimeSpan> beeps) : IAudioSource
{
    private static readonly TimeSpan BeepLength = TimeSpan.FromMilliseconds(150);
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public void Start()
    {
        _thread = new Thread(Run) { Name = "Grably test tone", IsBackground = true };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join();
    }

    private void Run()
    {
        var last = RecordingClock.PerformanceCounterNow();
        while (!_stop.IsCancellationRequested)
        {
            Thread.Sleep(10);
            var now = RecordingClock.PerformanceCounterNow();
            var frames = (int)((now - last) * AudioTrackFormat.SampleRate / TimeSpan.TicksPerSecond);
            if (frames == 0)
            {
                continue;
            }

            var samples = new float[frames * AudioTrackFormat.Channels];
            if (clock.At(last) is { } start)
            {
                for (var i = 0; i < frames; i++)
                {
                    var t = start + TimeSpan.FromTicks(i * TimeSpan.TicksPerSecond / AudioTrackFormat.SampleRate);
                    var value = beeps.Any(b => t >= b && t < b + BeepLength) ? 0.5f * MathF.Sin(2 * MathF.PI * 1000 * (float)t.TotalSeconds) : 0f;
                    samples[i * 2] = value;
                    samples[(i * 2) + 1] = value;
                }
            }

            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()), silent: false, last);
            last += (long)frames * TimeSpan.TicksPerSecond / AudioTrackFormat.SampleRate;
        }
    }
}
