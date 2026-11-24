using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Grably.Platform.Recording;

/// <summary>One camera picture: BGRA, opaque, tightly packed. <paramref name="Version"/> grows with every new picture.</summary>
public sealed record WebcamFrame(byte[] Pixels, int Width, int Height, long Version);

/// <summary>Where the newest camera picture can be read (by the on-screen bubble and the compositor).</summary>
public interface IWebcamFrames
{
    WebcamFrame? Latest { get; }
}

/// <summary>The camera cannot be used; the message says why and what to do.</summary>
public sealed class WebcamUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A camera through WinRT MediaCapture and a frame reader, at up to 1280x720 and about 30 frames per second, as BGRA
/// in memory. Thread-safe: frames arrive on a WinRT thread, <see cref="Latest"/> is read from anywhere.
/// </summary>
public sealed class WebcamSource : IWebcamFrames, IAsyncDisposable
{
    /// <summary>Pictures are written into a few buffers in turn (no 3.7 MB allocation per frame); readers copy within a frame time.</summary>
    private const int Buffers = 3;

    private readonly MediaCapture _capture;
    private readonly MediaFrameReader _reader;
    private readonly object _lock = new();
    private readonly byte[]?[] _buffers = new byte[]?[Buffers];
    private byte[] _raw = [];
    private WebcamFrame? _latest;
    private long _version;

    private WebcamSource(MediaCapture capture, MediaFrameReader reader)
    {
        _capture = capture;
        _reader = reader;
        _reader.FrameArrived += OnFrameArrived;
    }

    public WebcamFrame? Latest
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>The cameras that give colour video (id and name).</summary>
    public static async Task<IReadOnlyList<AudioDevice>> ListAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups.Where(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color))
            .Select(g => new AudioDevice(g.Id, g.DisplayName))
            .ToList();
    }

    /// <exception cref="WebcamUnavailableException">No camera, access is turned off, or it cannot be started.</exception>
    public static async Task<WebcamSource> StartAsync(string? cameraId)
    {
        MediaCapture? capture = null;
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var group = groups
                .Where(g => cameraId is null || g.Id == cameraId)
                .FirstOrDefault(g => g.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color))
                ?? throw new WebcamUnavailableException("No camera was found.");
            var info = group.SourceInfos.FirstOrDefault(i => i.SourceKind == MediaFrameSourceKind.Color && i.MediaStreamType == MediaStreamType.VideoRecord)
                ?? group.SourceInfos.First(i => i.SourceKind == MediaFrameSourceKind.Color);

            // Exclusive control lets Grably pick the format; when another app (a call) has the camera, share it.
            var exclusive = true;
            capture = await OpenAsync(group, MediaCaptureSharingMode.ExclusiveControl);
            if (capture is null)
            {
                exclusive = false;
                capture = await OpenAsync(group, MediaCaptureSharingMode.SharedReadOnly)
                    ?? throw new WebcamUnavailableException("The camera could not be opened. Another app may be using it.");
            }

            var source = capture.FrameSources.TryGetValue(info.Id, out var found) ? found : capture.FrameSources.Values.First();
            if (exclusive)
            {
                // Smooth first (at least 24 frames per second), then as sharp as possible up to 1280x720.
                var format = source.SupportedFormats
                    .Where(f => f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 720 && f.FrameRate.Denominator != 0)
                    .OrderByDescending(f => (double)f.FrameRate.Numerator / f.FrameRate.Denominator >= 24)
                    .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
                    .ThenBy(f => Math.Abs(((double)f.FrameRate.Numerator / f.FrameRate.Denominator) - 30))
                    .FirstOrDefault();
                if (format is not null)
                {
                    await source.SetFormatAsync(format);
                }
            }

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            var webcam = new WebcamSource(capture, reader);
            if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
            {
                await webcam.DisposeAsync();
                throw new WebcamUnavailableException("The camera did not start. Another app may be using it.");
            }

            return webcam;
        }
        catch (WebcamUnavailableException)
        {
            capture?.Dispose();
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            capture?.Dispose();
            throw new WebcamUnavailableException(
                "Grably may not use the camera. In Windows Settings, open Privacy & security > Camera and allow desktop apps to use it.", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any other failure leaves the camera out; it must never stop the recording.
            capture?.Dispose();
            throw new WebcamUnavailableException("The camera did not start.", ex);
        }
    }

    /// <summary>Opens the camera in this sharing mode, or null when that mode is refused (access denial is passed on).</summary>
    private static async Task<MediaCapture?> OpenAsync(MediaFrameSourceGroup group, MediaCaptureSharingMode sharing)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = sharing,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
            return capture;
        }
        catch (Exception ex) when (ex is not (UnauthorizedAccessException or OutOfMemoryException))
        {
            capture.Dispose();
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.FrameArrived -= OnFrameArrived;
        try
        {
            await _reader.StopAsync();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            // Already stopped (camera unplugged).
        }

        _reader.Dispose();
        _capture.Dispose();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } bitmap)
            {
                return;
            }

            using var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
            var source = bgra ?? bitmap;
            int width = source.PixelWidth, height = source.PixelHeight, rowBytes = width * 4;
            int stride;
            using (var locked = source.LockBuffer(BitmapBufferAccessMode.Read))
            {
                stride = Math.Max(rowBytes, locked.GetPlaneDescription(0).Stride);
            }

            // Copy as stored (rows may be padded), then pack the rows tightly into the next buffer in turn.
            if (_raw.Length < stride * height)
            {
                _raw = new byte[stride * height];
            }

            var raw = _raw.AsBuffer(0, stride * height);
            raw.Length = 0; // CopyToBuffer sets it to what it wrote: padded rows (stride) or packed ones
            source.CopyToBuffer(raw);
            var copiedStride = raw.Length >= (uint)(stride * height) ? stride : rowBytes;
            var slot = (int)((_version + 1) % Buffers);
            var pixels = _buffers[slot] is { } reused && reused.Length == rowBytes * height ? reused : new byte[rowBytes * height];
            _buffers[slot] = pixels;
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(_raw, y * copiedStride, pixels, y * rowBytes, rowBytes);
            }

            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255; // cameras leave alpha undefined; the picture is opaque
            }

            lock (_lock)
            {
                _latest = new WebcamFrame(pixels, width, height, ++_version);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An odd frame, or one arriving while closing: skip it (an exception here would end the app).
            System.Diagnostics.Debug.WriteLine(ex.GetType().Name);
        }
    }
}

/// <summary>A generated 640x360 picture (colour gradient with a moving bar), standing in for a camera in tests.</summary>
public sealed class SyntheticWebcam : IWebcamFrames
{
    private const int Width = 640;
    private const int Height = 360;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The colour of the gradient's left edge (0xRRGGBB), so a test can find the picture in a frame.</summary>
    public const int LeftColor = 0x2060E0;

    public WebcamFrame? Latest
    {
        get
        {
            var version = _clock.ElapsedMilliseconds / 33;
            var pixels = new byte[Width * Height * 4];
            var bar = (int)(version * 8 % Width);
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var i = ((y * Width) + x) * 4;
                    var t = x / (double)Width;
                    var onBar = Math.Abs(x - bar) < 6;
                    pixels[i] = onBar ? (byte)255 : (byte)(0xE0 * (1 - t)); // blue fades out
                    pixels[i + 1] = onBar ? (byte)255 : (byte)(0x60 + (0x80 * t));
                    pixels[i + 2] = onBar ? (byte)255 : (byte)(0x20 + (0xC0 * t));
                    pixels[i + 3] = 255;
                }
            }

            return new WebcamFrame(pixels, Width, Height, version);
        }
    }
}
