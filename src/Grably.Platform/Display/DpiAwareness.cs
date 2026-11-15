using Windows.Win32;

namespace Grably.Platform.Display;

/// <summary>
/// Capture code works in physical pixels, which needs a per-monitor DPI aware process. Grably.exe declares it in
/// its manifest; console tools and test hosts call <see cref="EnsurePerMonitorV2"/> first.
/// </summary>
public static class DpiAwareness
{
    /// <summary>Makes the process per-monitor (v2) DPI aware. Returns false if it was already set (that is fine).</summary>
    public static bool EnsurePerMonitorV2() =>
        PInvoke.SetProcessDpiAwarenessContext(
            Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
}
