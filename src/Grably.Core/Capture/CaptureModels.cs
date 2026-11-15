using Grably.Core.Geometry;
using Grably.Core.Imaging;

namespace Grably.Core.Capture;

/// <summary>One display. Bounds and work area are in physical pixels of the virtual desktop.</summary>
/// <param name="Index">Position in the enumeration, 0-based; the primary monitor is not necessarily 0.</param>
/// <param name="Dpi">Effective DPI (96 = 100 % scaling).</param>
public sealed record MonitorInfo(int Index, string DeviceName, PixelRect Bounds, PixelRect WorkArea, int Dpi, bool IsPrimary)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>A top-level window as seen on screen.</summary>
/// <param name="Handle">The HWND.</param>
/// <param name="Bounds">The visible frame (DWM extended frame bounds, without the invisible resize border).</param>
public sealed record WindowInfo(nint Handle, string Title, int ProcessId, string ProcessName, string ClassName, PixelRect Bounds);

/// <summary>Where a screenshot came from, recorded with the capture for the library.</summary>
public sealed record CaptureSource(string? WindowTitle, string? ProcessName);

public enum CaptureKind
{
    Region,
    Window,
    FullScreen,
    Scrolling,
}

/// <summary>A finished screenshot before it is delivered (clipboard, file, editor).</summary>
/// <param name="Area">Where on the virtual desktop the image came from.</param>
public sealed record CaptureResult(PixelBuffer Image, PixelRect Area, CaptureKind Kind, CaptureSource Source, DateTime CapturedAt);
