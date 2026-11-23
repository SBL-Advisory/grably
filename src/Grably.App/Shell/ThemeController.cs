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
        ApplyAccent(theme);
    }

    private static void ApplyAccent(ApplicationTheme theme) =>
        ApplicationAccentColorManager.Apply(Accent, theme, systemGlassColor: false, systemAccentColor: false);
}
