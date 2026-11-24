using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Grably.Platform.Recording;

/// <summary>
/// Windows.Graphics.Capture of a monitor or window. Each arriving frame is copied into a texture this class owns
/// and released at once (the pool has only two buffers). A window that is minimised sends no frames, so the last
/// one stays; a resized window recreates the pool at the new size.
/// </summary>
internal sealed class ScreenCaptureSource : IDisposable
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly GraphicsDevice _gpu;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private ID3D11Texture2D? _latest; // guarded by _gpu.Lock
    private long _frames;
    private bool _disposed; // guarded by _gpu.Lock

    private ScreenCaptureSource(GraphicsDevice gpu, GraphicsCaptureItem item)
    {
        _gpu = gpu;
        _item = item;
        _item.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRtDevice, PixelFormat, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        try
        {
            _session.IsBorderRequired = false; // the yellow frame Windows draws around a captured area
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // Older Windows or borderless capture not allowed: the border stays, but recording still works.
        }

        _session.IsCursorCaptureEnabled = true;
    }

    /// <summary>The window being recorded was closed.</summary>
    public event EventHandler? Closed;

    /// <summary>Frames delivered by Windows so far.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>The newest frame's texture (content size), or null before the first frame. Read under the GPU lock.</summary>
    public ID3D11Texture2D? Latest => _latest;

    /// <summary>Starts capturing. Asks for borderless capture first (allowed without asking for unpackaged apps).</summary>
    public static async Task<ScreenCaptureSource> StartAsync(GraphicsDevice gpu, CaptureTarget target)
    {
        try
        {
            await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // Not available on this build; the session setting below decides.
        }

        var item = target.Kind == CaptureTargetKind.Window ? WinRtInterop.ForWindow(target.Handle) : WinRtInterop.ForMonitor(target.Handle);
        var source = new ScreenCaptureSource(gpu, item);
        source._session.StartCapture();
        return source;
    }

    /// <summary>Waits until the first frame has arrived (a window that is minimised may never send one).</summary>
    public async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Frames == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        return Frames > 0;
    }

    public void Dispose()
    {
        lock (_gpu.Lock)
        {
            if (_disposed)
            {
                return;
            }

            // First, so a callback already running stops at the lock instead of touching what is freed below.
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }

        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object args)
    {
        try
        {
            CopyFrame(pool);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Runtime.InteropServices.COMException or SharpGen.Runtime.SharpGenException)
        {
            // A frame that arrived while the recording was closing, or a device problem: an unhandled exception on
            // this thread would end the app, and the compositor keeps showing the last good frame.
        }
    }

    private void CopyFrame(Direct3D11CaptureFramePool pool)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        var content = frame.ContentSize;
        using (var texture = WinRtInterop.TextureOf(frame.Surface))
        {
            var description = texture.Description;
            var width = (uint)Math.Clamp(content.Width, 1, (int)description.Width);
            var height = (uint)Math.Clamp(content.Height, 1, (int)description.Height);
            lock (_gpu.Lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_latest is null || _latest.Description.Width != width || _latest.Description.Height != height)
                {
                    _latest?.Dispose();
                    _latest = _gpu.Device.CreateTexture2D(new Texture2DDescription(
                        Format.B8G8R8A8_UNorm, width, height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Default,
                        CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
                }

                // Only the content: a window that shrank leaves stale pixels in the rest of the pool surface.
                _gpu.Context.CopySubresourceRegion(_latest, 0, 0, 0, 0, texture, 0, new Box(0, 0, 0, (int)width, (int)height, 1));
            }
        }

        Interlocked.Increment(ref _frames);
        if ((content.Width != _poolSize.Width || content.Height != _poolSize.Height) && !Volatile.Read(ref _disposed))
        {
            _poolSize = content;
            pool.Recreate(_gpu.WinRtDevice, PixelFormat, 2, content);
        }
    }
}
