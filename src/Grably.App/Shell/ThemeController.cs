using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.Core;
using Grably.Core.Settings;
using Grably.Platform.Shell;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Grably.App.Shell;

/// <summary>Applies the light/dark theme from settings (or Windows) and the SBL colours (see assets/brand/README.md).</summary>
public sealed class ThemeController(SettingsStore settings)
{
    /// <summary>SBL navy, the UI accent: buttons, toggles, selection. The dark theme uses lighter shades of it.</summary>
    public static readonly Color Accent = Rgb(BrandColors.Navy);

    // WPF-UI's four accent shades: SystemAccentColor, then Primary, Secondary and Tertiary. Buttons and toggles fill
    // with Primary in the light theme and Secondary in the dark one, links use Secondary, and text on the accent turns
    // dark when Secondary is bright. So the light theme fills with SBL navy itself (white text), and the dark theme
    // with a light navy (black text; links 6.8:1 on #202020).
    private static readonly Color[] LightAccents = [Accent, Accent, Rgb(0x1D3371), Rgb(0x172A5E)];
    private static readonly Color[] DarkAccents = [Accent, Rgb(BrandColors.NavyMid), Rgb(0x8FA6E8), Rgb(0xB7C6F2)];

    /// <summary>SBL orange, for marks drawn over screenshots and video: outlines, handles, the trim selection.</summary>
    public static readonly Color Mark = Rgb(BrandColors.Orange);

    /// <summary>Application resource holding the SBL logo for the current theme: white on dark, full colour on light.</summary>
    public const string SblLogoKey = "SblLogo";

    private static ImageSource? _sblLogoColor;
    private static ImageSource? _sblLogoWhite;

    private bool _followSystem;

    /// <param name="overrideTheme">"light" or "dark" to force a theme (UI snapshots); null uses the settings.</param>
    public void Initialize(string? overrideTheme = null)
    {
        var preference = overrideTheme?.ToLowerInvariant() switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => settings.Current.General.Theme,
        };
        _followSystem = preference == AppTheme.System && overrideTheme is null;
        Apply(Resolve(preference));

        // Theme switches (ours or Windows') rebuild the theme resources; put the brand accent and logo back each time.
        ApplicationThemeManager.Changed += (theme, _) => ApplyBrand(theme);
    }

    /// <summary>Open Fluent windows, so a new theme choice can start or stop following Windows in each.</summary>
    private readonly List<Window> _windows = [];

    /// <summary>Lets a Fluent window follow Windows light/dark changes when the user picked "System".</summary>
    public void Attach(Window window)
    {
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        if (_followSystem)
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: false);
        }
    }

    /// <summary>Applies a new theme choice from the settings window, to the app and every open window.</summary>
    public void ApplyPreference(AppTheme preference)
    {
        var followSystem = preference == AppTheme.System;
        Apply(Resolve(preference));
        if (followSystem == _followSystem)
        {
            return; // the windows already watch (or ignore) Windows' theme as they should
        }

        _followSystem = followSystem;
        foreach (var window in _windows)
        {
            if (followSystem)
            {
                SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: false);
            }
            else
            {
                SystemThemeWatcher.UnWatch(window);
            }
        }
    }

    private static ApplicationTheme Resolve(AppTheme preference) => preference switch
    {
        AppTheme.Light => ApplicationTheme.Light,
        AppTheme.Dark => ApplicationTheme.Dark,
        _ => WindowsTheme.AppsUseLightTheme() ? ApplicationTheme.Light : ApplicationTheme.Dark,
    };

    private static void Apply(ApplicationTheme theme)
    {
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false);
        ApplyBrand(theme);
    }

    private static void ApplyBrand(ApplicationTheme theme)
    {
        // This overload reads the theme from ApplicationThemeManager, which has switched by the time we get here.
        var accents = theme == ApplicationTheme.Dark ? DarkAccents : LightAccents;
        ApplicationAccentColorManager.Apply(accents[0], accents[1], accents[2], accents[3]);

        // High contrast themes come with light or dark backgrounds; the logo follows the window colour there.
        var darkBackground = theme == ApplicationTheme.Dark
            || (theme == ApplicationTheme.HighContrast && IsDark(SystemColors.WindowColor));
        Application.Current.Resources[SblLogoKey] = darkBackground
            ? _sblLogoWhite ??= LoadSblLogo("sbl-logo-white.png")
            : _sblLogoColor ??= LoadSblLogo("sbl-logo-color.png");
    }

    private static bool IsDark(Color c) => (0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B) < 128;

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // Loaded on first use, not in a static initializer: pack URIs only work once the WPF Application exists.
    private static BitmapImage LoadSblLogo(string file)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/Assets/Sbl/{file}");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
