using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Shell;

/// <summary>Reads the Windows light/dark preferences and the small-icon size used by the notification area.</summary>
public static class WindowsTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when the taskbar (and so the tray) is light. Missing value means dark, the Windows default.</summary>
    public static bool IsTaskbarLight() => ReadFlag("SystemUsesLightTheme", fallback: false);

    /// <summary>True when apps should use the light theme.</summary>
    public static bool AppsUseLightTheme() => ReadFlag("AppsUseLightTheme", fallback: true);

    /// <summary>Pixel size of a small icon (tray icon) for the system DPI: 16 at 100 %, 24 at 150 %, 32 at 200 %.</summary>
    public static int SmallIconSize()
    {
        var dpi = PInvoke.GetDpiForSystem();
        var size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, dpi);
        return size > 0 ? size : 16;
    }

    private static bool ReadFlag(string name, bool fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is int value ? value != 0 : fallback;
    }
}
