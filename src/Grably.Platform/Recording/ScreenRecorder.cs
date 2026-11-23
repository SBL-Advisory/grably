using System.Diagnostics;
using Grably.Core.Geometry;
using Grably.Core.Recording;
using Grably.Core.Settings;
using Microsoft.Extensions.Logging;
using Windows.Win32;

namespace Grably.Platform.Recording;

public sealed record RecorderOptions
{
    /// <summary>What to record; null records a test pattern (colour bars) of <see cref="PatternSize"/>.</summary>
    public CaptureTarget? Target { get; init; }

    public (int Width, int Height) PatternSize { get; init; } = (1280, 720);

    public int FrameRate { get; init; } = 30;

    public VideoQuality Quality { get; init; } = VideoQuality.High;

    /// <summary>Scale taller sources down to this height.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>Convert to NV12 on the GPU (the default); false reads back BGRA and lets ffmpeg convert.</summary>
    public bool PreferNv12 { get; init; } = true;

    /// <summary>Skip the probe and use this encoder.</summary>
    public VideoEncoder? Encoder { get; init; }

    public required string Ffmpeg { get; init; }

    /// <summary>Where the session folder (session.json and the Matroska intermediate) is created.</summary>
    public required string SessionsFolder { get; init; }
}

/// <param name="Composed">Frames actually drawn and read back.</param>
/// <param name="Repeats">Frames written again because the next one was late (the compositor fell behind the clock).</param>
/// <param name="Dropped">Frames not read back because the encoder was behind (the previous frame was written instead).</param>
/// <param name="DroppedAtStart">Of those, the ones in the first second, while the encoder starts up (hidden by the countdown).</param>
public sealed record RecordingResult(
    RecordingSession Session, string SessionFolder, string VideoFile, long Frames, long Composed, long Repeats, long Dropped,
    long DroppedAtStart, TimeSpan Duration, RawPixelFormat Format, double AverageComposeMilliseconds, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Records the screen (or a test pattern) to a crash-safe Matroska file:
/// Windows.Graphics.Capture, then a compositor thread that makes frame N at time N / fps from one clock, then
/// readback (NV12 converted on the GPU, or BGRA), then a writer thread feeding ffmpeg's stdin. A small pool of frame
/// buffers sits between the two threads: when the encoder falls behind, frames are repeated instead of read back,
/// so capture never waits for the encoder and the video always lasts as long as the recording.
/// </summary>
public sealed class ScreenRecorder : IAsyncDisposable
{
    /// <summary>Frame buffers between the compositor and the encoder: about 128 MB, enough to ride out the encoder's start-up.</summary>
    private const long PoolBytes = 128L * 1024 * 1024;

    private readonly RecorderOptions _options;
    private readonly ILogger _logger;
    private readonly GraphicsDevice _gpu;
    private readonly ScreenCaptureSource? _source;
    private readonly FrameCompositor _compositor;
    private readonly FfmpegProcess _ffmpeg;
    private readonly FramePacer _pacer;
    private readonly Stopwatch _clock = new();
    private readonly object _queueLock = new();
    private readonly Queue<Frame> _queue = new();
    private readonly Stack<byte[]> _free = new();
    private readonly Thread _composeThread;
    private readonly Thread _writeThread;
    private Frame? _lastQueued;
    private Frame? _writing;
    private int _extraForWriting;
    private volatile bool _stopping;
    private int _disposed;
    private int _failedRaised;
    private bool _composeDone;
    private long _composed;
    private long _dropped;
    private long _droppedAtStart;
    private double _composeMilliseconds;
    private string? _error;

    private ScreenRecorder(
        RecorderOptions options, ILogger logger, GraphicsDevice gpu, ScreenCaptureSource? source, FrameCompositor compositor,
        FfmpegProcess ffmpeg, RecordingSession session, string folder)
    {
        _options = options;
        _logger = logger;
        _gpu = gpu;
        _source = source;
        _compositor = compositor;
        _ffmpeg = ffmpeg;
        Session = session;
        SessionFolder = folder;
        _pacer = new FramePacer(options.FrameRate);
        var poolSize = (int)Math.Clamp(PoolBytes / compositor.FrameBytes, 4, options.FrameRate);
        for (var i = 0; i < poolSize; i++)
        {
            _free.Push(new byte[compositor.FrameBytes]);
        }

        if (source is not null)
        {
            source.Closed += (_, _) => SourceClosed?.Invoke(this, EventArgs.Empty);
        }

        _composeThread = new Thread(ComposeLoop) { Name = "Grably compositor", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _writeThread = new Thread(WriteLoop) { Name = "Grably encoder feed", IsBackground = true };
    }

    /// <summary>The recorded window was closed; the caller should stop.</summary>
    public event EventHandler? SourceClosed;

    /// <summary>
    /// Recording stopped by itself (ffmpeg exited, frames could not be made). Raised once, on a background thread;
    /// the caller should call <see cref="StopAsync"/>, which keeps what was recorded.
    /// </summary>
    public event EventHandler<string>? Failed;

    public RecordingSession Session { get; private set; }

    public string SessionFolder { get; }

    public string VideoFile => Path.Combine(SessionFolder, RecordingSession.VideoFileName);

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Opens the capture, picks the encoder at the output size, starts ffmpeg and the frame clock.</summary>
    /// <exception cref="InvalidOperationException">Nothing to record, or no encoder works at any size.</exception>
    public static async Task<ScreenRecorder> StartAsync(RecorderOptions options, EncoderProbe probe, ILogger logger, CancellationToken cancellationToken = default)
    {
        var gpu = GraphicsDevice.Create();
        ScreenCaptureSource? source = null;
        FrameCompositor? compositor = null;
        try
        {
            int sourceWidth, sourceHeight;
            if (options.Target is { } target)
            {
                source = await ScreenCaptureSource.StartAsync(gpu, target);
                if (!await source.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3)))
                {
                    throw new InvalidOperationException("Nothing arrived to record. Is the window minimised?");
                }

                lock (gpu.Lock)
                {
                    var content = new PixelRect(0, 0, (int)source.Latest!.Description.Width, (int)source.Latest.Description.Height);
                    var area = target.Crop is { } crop ? crop.Intersect(content) : content;
                    (sourceWidth, sourceHeight) = (area.Width, area.Height);
                }
            }
            else
            {
                (sourceWidth, sourceHeight) = options.PatternSize;
            }

            var (width, height, encoder) = await ChooseSizeAndEncoderAsync(options, probe, sourceWidth, sourceHeight, cancellationToken);
            lock (gpu.Lock)
            {
                compositor = new FrameCompositor(gpu, width, height, options.FrameRate, options.PreferNv12);
            }

            var now = DateTime.Now;
            var session = new RecordingSession
            {
                Id = RecordingSession.NewId(now),
                StartedUtc = now.ToUniversalTime(),
                Target = options.Target?.Description ?? "Test pattern",
                Width = width,
                Height = height,
                FrameRate = options.FrameRate,
                Encoder = encoder.Name,
            };
            var folder = Path.Combine(options.SessionsFolder, session.Id);
            session.Save(folder);
            FfmpegProcess? ffmpeg = null;
            try
            {
                ffmpeg = FfmpegProcess.Start(
                    options.Ffmpeg,
                    FfmpegArguments.Encode(encoder, compositor.OutputFormat, width, height, options.FrameRate, options.Quality, Path.Combine(folder, RecordingSession.VideoFileName)),
                    withInput: true);
                logger.LogInformation(
                    "Recording {Target} from {SourceWidth}x{SourceHeight} as {Width}x{Height} at {Fps} fps with {Encoder} ({Format})",
                    options.Target?.LogDescription ?? "test pattern", sourceWidth, sourceHeight, width, height, options.FrameRate, encoder.Name,
                    compositor.OutputFormat);

                var recorder = new ScreenRecorder(options, logger, gpu, source, compositor, ffmpeg, session, folder);
                recorder._clock.Start();
                recorder._composeThread.Start();
                recorder._writeThread.Start();
                return recorder;
            }
            catch
            {
                // Never leave an orphaned ffmpeg, or a session that crash recovery would take for an interrupted recording.
                ffmpeg?.Dispose();
                (session with { State = SessionState.Failed }).Save(folder);
                throw;
            }
        }
        catch
        {
            compositor?.Dispose();
            source?.Dispose();
            gpu.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Stops the clock, writes the last frames, waits for ffmpeg to finish the file and closes everything. An encoder
    /// that stops taking frames is killed after a while, so stopping never hangs (what was written stays playable).
    /// </summary>
    public async Task<RecordingResult> StopAsync()
    {
        if (!await JoinWorkersAsync(TimeSpan.FromSeconds(30)))
        {
            _error ??= $"ffmpeg stopped taking frames and was stopped: {_ffmpeg.Errors}";
        }

        if (!await _ffmpeg.WaitAsync(TimeSpan.FromSeconds(60)))
        {
            _error ??= "ffmpeg did not finish the file in time.";
        }
        else if (_ffmpeg.ExitCode != 0)
        {
            _error ??= $"ffmpeg failed ({_ffmpeg.ExitCode}): {_ffmpeg.Errors}";
        }

        Session = Session with { State = _error is null ? SessionState.Finished : SessionState.Failed };
        Session.Save(SessionFolder);
        if (_error is not null)
        {
            _logger.LogError("Recording failed: {Error}", _error);
        }

        var composed = Interlocked.Read(ref _composed);
        var result = new RecordingResult(
            Session, SessionFolder, VideoFile, _pacer.Frames, composed, _pacer.Repeats, Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _droppedAtStart), _pacer.Duration,
            _compositor.OutputFormat, composed == 0 ? 0 : _composeMilliseconds / composed, _error);
        _logger.LogInformation(
            "Recording stopped: {Frames} frames ({Duration}), {Repeats} repeated late, {Dropped} dropped for the encoder, {Compose:0.0} ms per frame",
            result.Frames, result.Duration, result.Repeats, result.Dropped, result.AverageComposeMilliseconds);
        await DisposeAsync();
        return result;
    }

    /// <summary>Stops the worker threads (killing ffmpeg if it hangs them) and only then frees what they use.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await JoinWorkersAsync(TimeSpan.FromSeconds(5));
        _ffmpeg.Dispose();
        _source?.Dispose(); // before the device: a late capture callback must find the source closed
        lock (_gpu.Lock)
        {
            _compositor.Dispose();
        }

        _gpu.Dispose();
    }

    /// <summary>
    /// Stops the frame clock and waits for both threads. If the writer is stuck on a full pipe (ffmpeg stopped
    /// reading), ffmpeg is killed so the write fails and the thread ends. False when that was needed.
    /// </summary>
    private async Task<bool> JoinWorkersAsync(TimeSpan writerLimit)
    {
        _stopping = true;
        var clean = await Task.Run(() =>
        {
            _composeThread.Join(TimeSpan.FromSeconds(10));
            if (_writeThread.Join(writerLimit))
            {
                return true;
            }

            _ffmpeg.Kill();
            _writeThread.Join(TimeSpan.FromSeconds(10));
            return false;
        });
        return clean;
    }

    /// <summary>Remuxes the Matroska intermediate into an MP4 (no re-encoding) with its index at the front.</summary>
    /// <param name="codec">h264 or hevc (HEVC gets the hvc1 tag Apple players need).</param>
    public static async Task<string?> SaveAsMp4Async(string ffmpeg, string matroska, string mp4, string codec = "h264")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mp4))!);
        var (code, errors) = await FfmpegProcess.RunAsync(ffmpeg, FfmpegArguments.Remux(matroska, mp4, codec), TimeSpan.FromMinutes(10));
        return code == 0 ? null : $"ffmpeg could not write {Path.GetFileName(mp4)} ({code}): {errors}";
    }

    /// <summary>
    /// The output size and the first encoder that works at it. When none works at full size (NVENC H.264 stops at
    /// 4096 wide, for example), smaller sizes are tried before giving up.
    /// </summary>
    private static async Task<(int Width, int Height, VideoEncoder Encoder)> ChooseSizeAndEncoderAsync(
        RecorderOptions options, EncoderProbe probe, int sourceWidth, int sourceHeight, CancellationToken cancellationToken)
    {
        int?[] widthLimits = [null, 4096, 3840, 1920];
        foreach (var maxWidth in widthLimits)
        {
            var (width, height) = OutputSize.For(sourceWidth, sourceHeight, options.MaxHeight, maxWidth);
            if (maxWidth is not null && width >= sourceWidth)
            {
                continue; // this limit changes nothing
            }

            var encoder = options.Encoder ?? await probe.ChooseAsync(width, height, cancellationToken);
            if (encoder is not null)
            {
                return (width, height, encoder);
            }
        }

        throw new InvalidOperationException("No video encoder works on this PC. The log lists what each one reported.");
    }

    private void ComposeLoop()
    {
        PInvoke.timeBeginPeriod(1); // Sleep accurate to about a millisecond while recording
        try
        {
            while (!_stopping)
            {
                var due = _pacer.NextDue;
                var wait = due - _clock.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(2))
                {
                    Thread.Sleep(wait - TimeSpan.FromMilliseconds(1));
                }

                while (_clock.Elapsed < due && !_stopping)
                {
                    Thread.SpinWait(64);
                }

                if (_stopping)
                {
                    break;
                }

                var buffer = Rent();
                if (buffer is not null)
                {
                    var started = Stopwatch.GetTimestamp();
                    lock (_gpu.Lock)
                    {
                        if (_source is null)
                        {
                            _compositor.DrawTestPattern(_pacer.Frames);
                        }
                        else
                        {
                            _compositor.Draw(_source.Latest, _options.Target!.Crop);
                        }

                        _compositor.ReadInto(buffer);
                    }

                    _composeMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Interlocked.Increment(ref _composed);
                }

                Enqueue(buffer, _pacer.Take(_clock.Elapsed));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Recording compositor failed");
            Fail($"Making frames failed: {ex.Message}");
        }
        finally
        {
            PInvoke.timeEndPeriod(1);
            lock (_queueLock)
            {
                _composeDone = true;
                Monitor.PulseAll(_queueLock);
            }
        }
    }

    private void WriteLoop()
    {
        var input = _ffmpeg.Input;
        var bytes = _compositor.FrameBytes;
        try
        {
            while (true)
            {
                Frame frame;
                lock (_queueLock)
                {
                    while (_queue.Count == 0 && !_composeDone)
                    {
                        Monitor.Wait(_queueLock);
                    }

                    if (_queue.Count == 0)
                    {
                        break;
                    }

                    frame = _queue.Dequeue();
                    if (ReferenceEquals(frame, _lastQueued))
                    {
                        _lastQueued = null;
                    }

                    _writing = frame;
                }

                var copies = frame.Copies;
                while (copies > 0)
                {
                    for (var i = 0; i < copies; i++)
                    {
                        input.Write(frame.Buffer, 0, bytes);
                    }

                    lock (_queueLock)
                    {
                        copies = _extraForWriting; // frames repeated while this one was being written
                        _extraForWriting = 0;
                        if (copies == 0)
                        {
                            _writing = null;
                            _free.Push(frame.Buffer);
                        }
                    }
                }
            }

            input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!_stopping || _ffmpeg.HasExited)
            {
                _logger.LogError(ex, "Writing frames to ffmpeg failed");
                Fail($"ffmpeg stopped taking frames: {_ffmpeg.Errors}");
            }

            _stopping = true;
        }
        finally
        {
            try
            {
                input.Close(); // ffmpeg finishes the file when its input ends
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already broken or killed.
            }
        }
    }

    private void Fail(string error)
    {
        _error ??= error;
        _stopping = true;
        if (Interlocked.Exchange(ref _failedRaised, 1) == 0)
        {
            Failed?.Invoke(this, error);
        }
    }

    private byte[]? Rent()
    {
        lock (_queueLock)
        {
            return _free.Count > 0 ? _free.Pop() : null;
        }
    }

    /// <summary>Queues a frame; without a buffer (the encoder is behind) the newest frame is repeated instead.</summary>
    private void Enqueue(byte[]? buffer, int copies)
    {
        lock (_queueLock)
        {
            if (buffer is not null)
            {
                var frame = new Frame(buffer, copies);
                _queue.Enqueue(frame);
                _lastQueued = frame;
                Monitor.Pulse(_queueLock);
                return;
            }

            _dropped += copies;
            if (_clock.Elapsed < TimeSpan.FromSeconds(1))
            {
                _droppedAtStart += copies;
            }
            if (_lastQueued is not null)
            {
                _lastQueued.Copies += copies;
            }
            else if (_writing is not null)
            {
                _extraForWriting += copies;
            }
        }
    }

    private sealed class Frame(byte[] buffer, int copies)
    {
        public byte[] Buffer { get; } = buffer;

        public int Copies { get; set; } = copies;
    }
}
