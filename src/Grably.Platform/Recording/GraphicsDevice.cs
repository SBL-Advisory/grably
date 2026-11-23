using Vortice.Direct2D1;
using Vortice.Direct3D;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace Grably.Platform.Recording;

/// <summary>
/// The Direct3D 11 device a recording runs on, with its Direct2D device (for drawing frames and, later, cursor
/// effects) and the WinRT wrapper that screen capture needs. The immediate context is shared by the capture
/// callback and the compositor thread, so every use of it takes <see cref="Lock"/>.
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, bool supportsVideo)
    {
        Device = device;
        Context = context;
        SupportsVideo = supportsVideo;
        using (var multithread = context.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true); // capture callbacks and Direct2D reach the device from other threads
        }

        WinRtDevice = WinRtInterop.CreateWinRtDevice(device);
        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        D2DDevice = D2DFactory.CreateDevice(dxgi);
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    /// <summary>Whether the device has video processing (GPU BGRA to NV12); not on some virtual machines.</summary>
    public bool SupportsVideo { get; }

    public IDirect3DDevice WinRtDevice { get; }

    public ID2D1Factory1 D2DFactory { get; }

    public ID2D1Device D2DDevice { get; }

    /// <summary>Taken around every use of <see cref="Context"/>.</summary>
    public object Lock { get; } = new();

    /// <summary>The hardware device with video support, else without it, else the WARP software rasterizer.</summary>
    public static GraphicsDevice Create()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        foreach (var (driver, video) in new[] { (DriverType.Hardware, true), (DriverType.Hardware, false), (DriverType.Warp, false) })
        {
            var flags = DeviceCreationFlags.BgraSupport | (video ? DeviceCreationFlags.VideoSupport : DeviceCreationFlags.None);
            if (D3D11.D3D11CreateDevice(IntPtr.Zero, driver, flags, levels, out var device, out var context).Success)
            {
                return new GraphicsDevice(device!, context!, video);
            }
        }

        throw new NotSupportedException("No Direct3D 11 device is available for recording.");
    }

    public void Dispose()
    {
        D2DDevice.Dispose();
        D2DFactory.Dispose();
        (WinRtDevice as IDisposable)?.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
