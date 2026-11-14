using System.Windows;
using System.Windows.Media;
using Grably.Core.Settings;
using Grably.Platform.Shell;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Grably.App.Shell;

/// <summary>Applies the light/dark theme from settings (or Windows) and the Grably teal accent.</summary>
public sealed class ThemeController(SettingsStore settings)
{
    /// <summary>Brand teal, see assets/brand/README.md.</summary>
    public static readonly Color Accent = Color.FromRgb(0x0E, 0x9A, 0xA7);

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

        // Theme switches (ours or Windows') rebuild the theme resources; put the brand accent back each time.
        ApplicationThemeManager.Changed += (theme, _) => ApplyAccent(theme);
    }

    /// <summary>Lets a Fluent window follow Windows light/dark changes when the user picked "System".</summary>
    public void Attach(Window window)
    {
        if (_followSystem)
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: false);
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
        ApplyAccent(theme);
    }

    private static void ApplyAccent(ApplicationTheme theme) =>
        ApplicationAccentColorManager.Apply(Accent, theme, systemGlassColor: false, systemAccentColor: false);
}
