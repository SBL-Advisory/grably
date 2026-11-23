using System.Runtime.InteropServices;
using Grably.Core.Geometry;
using Grably.Core.Recording;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace Grably.Platform.Recording;

/// <summary>
/// Makes each output frame: draws the newest captured frame (cropped, scaled to fit, letterboxed) into a fixed-size
/// texture with Direct2D, where cursor effects and the webcam will be drawn too (M8), then reads it back for
/// ffmpeg. Reading back NV12 converted on the GPU moves far less data and spares ffmpeg the colour conversion; BGRA
/// is the fallback for devices without video processing. Call everything under the GPU lock.
/// </summary>
internal sealed class FrameCompositor : IDisposable
{
    private readonly GraphicsDevice _gpu;
    private readonly ID3D11Texture2D _output;
    private readonly ID2D1DeviceContext _d2d;
    private readonly ID2D1Bitmap1 _target;
    private readonly ID3D11Texture2D _staging;
    private readonly Nv12Converter? _nv12;
    private ID3D11Texture2D? _sourceTexture;
    private ID2D1Bitmap1? _sourceBitmap;

    public FrameCompositor(GraphicsDevice gpu, int width, int height, int fps, bool preferNv12)
    {
        _gpu = gpu;
        Width = width;
        Height = height;
        _output = gpu.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource,
            ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _d2d = gpu.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        using (var surface = _output.QueryInterface<IDXGISurface>())
        {
            _target = _d2d.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        }

        _d2d.Target = _target;
        if (preferNv12 && gpu.SupportsVideo)
        {
            try
            {
                _nv12 = new Nv12Converter(gpu, _output, width, height, fps);
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or NotSupportedException)
            {
                _nv12 = null; // no NV12 output on this device: read back BGRA
            }
        }

        OutputFormat = _nv12 is null ? RawPixelFormat.Bgra : RawPixelFormat.Nv12;
        _staging = _nv12 is null
            ? gpu.Device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging,
                CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None))
            : _nv12.Staging;
    }

    public int Width { get; }

    public int Height { get; }

    public RawPixelFormat OutputFormat { get; }

    /// <summary>Bytes of one frame as read back.</summary>
    public int FrameBytes => OutputFormat == RawPixelFormat.Nv12 ? Width * Height * 3 / 2 : Width * Height * 4;

    /// <summary>Draws <paramref name="source"/> (or black before the first frame) into the output frame.</summary>
    /// <param name="crop">The part of the source to show, in its pixels; null shows all of it.</param>
    public void Draw(ID3D11Texture2D? source, PixelRect? crop)
    {
        _d2d.BeginDraw();
        _d2d.Clear(new Color4(0, 0, 0, 1));
        if (source is not null)
        {
            if (!ReferenceEquals(source, _sourceTexture))
            {
                _sourceBitmap?.Dispose();
                using var surface = source.QueryInterface<IDXGISurface>();
                _sourceBitmap = _d2d.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                    new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.None));
                _sourceTexture = source;
            }

            var content = new PixelRect(0, 0, (int)source.Description.Width, (int)source.Description.Height);
            var shown = crop is { } c ? c.Intersect(content) : content;

            // An odd width or height was rounded down to even: drop that pixel instead of resampling every frame,
            // which would soften all the text.
            if (shown.Width - Width is 0 or 1 && shown.Height - Height is 0 or 1)
            {
                shown = shown with { Width = Width, Height = Height };
            }

            if (!shown.IsEmpty)
            {
                // Fit inside the frame keeping the shape; a window that was resized is letterboxed, never stretched.
                var scale = Math.Min((double)Width / shown.Width, (double)Height / shown.Height);
                var w = shown.Width * scale;
                var h = shown.Height * scale;
                var x = (Width - w) / 2;
                var y = (Height - h) / 2;
                _d2d.DrawBitmap(
                    _sourceBitmap!,
                    new Rect((float)x, (float)y, (float)w, (float)h),
                    1,
                    scale == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityCubic,
                    new Rect(shown.X, shown.Y, shown.Width, shown.Height),
                    null);
            }
        }

        _d2d.EndDraw();
    }

    /// <summary>Colour bars, with a white square that moves one step per frame so frames can be told apart.</summary>
    /// <param name="flash">The whole frame white instead (the sync test's visual signal).</param>
    public void DrawTestPattern(long frame, bool flash = false)
    {
        _d2d.BeginDraw();
        if (flash)
        {
            _d2d.Clear(new Color4(1, 1, 1, 1));
            _d2d.EndDraw();
            return;
        }

        var barWidth = (float)Width / TestPattern.Bars.Count;
        for (var i = 0; i < TestPattern.Bars.Count; i++)
        {
            using var brush = _d2d.CreateSolidColorBrush(ToColor(TestPattern.Bars[i]));
            _d2d.FillRectangle(new Rect(i * barWidth, 0, barWidth, Height * 0.8f), brush);
        }

        using (var dark = _d2d.CreateSolidColorBrush(new Color4(0.1f, 0.1f, 0.1f, 1)))
        {
            _d2d.FillRectangle(new Rect(0, Height * 0.8f, Width, Height * 0.2f), dark);
        }

        using (var white = _d2d.CreateSolidColorBrush(new Color4(1, 1, 1, 1)))
        {
            var size = Height * 0.1f;
            _d2d.FillRectangle(new Rect(frame * 4 % Math.Max(1, (int)(Width - size)), Height * 0.85f, size, size), white);
        }

        _d2d.EndDraw();
    }

    /// <summary>Copies the frame just drawn into <paramref name="buffer"/> (<see cref="FrameBytes"/> long), tightly packed.</summary>
    public unsafe void ReadInto(byte[] buffer)
    {
        if (_nv12 is not null)
        {
            _nv12.Convert();
        }
        else
        {
            _gpu.Context.CopyResource(_staging, _output);
        }

        var map = _gpu.Context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = OutputFormat == RawPixelFormat.Nv12 ? Width : Width * 4;
            var rows = OutputFormat == RawPixelFormat.Nv12 ? Height * 3 / 2 : Height; // NV12: the UV rows follow the Y rows
            for (var y = 0; y < rows; y++)
            {
                Marshal.Copy(map.DataPointer + (nint)(y * (long)map.RowPitch), buffer, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _gpu.Context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        _sourceBitmap?.Dispose();
        _target.Dispose();
        _d2d.Dispose();
        if (_nv12 is null)
        {
            _staging.Dispose();
        }

        _nv12?.Dispose();
        _output.Dispose();
    }

    private static Color4 ToColor(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);

    /// <summary>BGRA to NV12 (BT.709, limited range) with the GPU's video processor, plus a staging copy to read.</summary>
    private sealed class Nv12Converter : IDisposable
    {
        private readonly GraphicsDevice _gpu;
        private readonly ID3D11VideoDevice _videoDevice;
        private readonly ID3D11VideoContext _videoContext;
        private readonly ID3D11VideoProcessorEnumerator _enumerator;
        private readonly ID3D11VideoProcessor _processor;
        private readonly ID3D11VideoProcessorInputView _input;
        private readonly ID3D11Texture2D _nv12;
        private readonly ID3D11VideoProcessorOutputView _outputView;

        public Nv12Converter(GraphicsDevice gpu, ID3D11Texture2D source, int width, int height, int fps)
        {
            _gpu = gpu;
            _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational((uint)fps, 1),
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputFrameRate = new Rational((uint)fps, 1),
                OutputWidth = (uint)width,
                OutputHeight = (uint)height,
                Usage = VideoUsage.PlaybackNormal,
            };
            _videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
            if ((_enumerator.CheckVideoProcessorFormat(Format.NV12) & VideoProcessorFormatSupport.Output) == 0)
            {
                throw new NotSupportedException("The video processor cannot output NV12.");
            }

            _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();
            _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            }, out _input).CheckError();
            _nv12 = gpu.Device.CreateTexture2D(new Texture2DDescription(
                Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget, ResourceUsage.Default,
                CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            _videoDevice.CreateVideoProcessorOutputView(_nv12, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            }, out _outputView).CheckError();
            Staging = gpu.Device.CreateTexture2D(new Texture2DDescription(
                Format.NV12, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging,
                CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));

            // Screen pixels are full-range RGB; the video is BT.709 with the 16-235 range players expect.
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 2 });
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = 1 });
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false); // no "enhancements"
        }

        public ID3D11Texture2D Staging { get; }

        public void Convert()
        {
            _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = _input }]);
            _gpu.Context.CopyResource(Staging, _nv12);
        }

        public void Dispose()
        {
            Staging.Dispose();
            _outputView.Dispose();
            _nv12.Dispose();
            _input.Dispose();
            _processor.Dispose();
            _enumerator.Dispose();
            _videoContext.Dispose();
            _videoDevice.Dispose();
        }
    }
}
