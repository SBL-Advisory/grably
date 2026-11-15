using System.Windows;
using Grably.Core.Hotkeys;
using Grably.Core.Settings;
using Grably.Platform.Hotkeys;
using Microsoft.Extensions.Logging;

namespace Grably.App.Shell;

/// <summary>Registers the hotkeys from settings and routes presses to <see cref="AppCommands"/> on the UI thread.</summary>
public sealed class HotkeyController(
    GlobalHotkeyService hotkeys, SettingsStore settings, AppCommands commands, ILogger<HotkeyController> logger)
{
    /// <summary>Results of the last registration, for the settings page to show conflicts.</summary>
    public IReadOnlyList<HotkeyRegistration> Registrations { get; private set; } = [];

    public void Start()
    {
        hotkeys.HotkeyPressed += (_, e) => Application.Current.Dispatcher.BeginInvoke(() => commands.Execute(e.Action));
        settings.Saved += (_, _) => Application.Current.Dispatcher.BeginInvoke(ApplyFromSettings);
        ApplyFromSettings();
    }

    private void ApplyFromSettings()
    {
        // Pause is only registered while recording, so it does not hold on to the key combination otherwise.
        var bindings = settings.Current.Hotkeys.Where(b => b.Key != HotkeyAction.PauseRecording);
        Registrations = hotkeys.Apply(bindings);
        var failed = Registrations.Where(r => !r.Succeeded).ToList();
        if (failed.Count > 0)
        {
            logger.LogWarning(
                "{Count} hotkey(s) could not be registered: {Hotkeys}",
                failed.Count, string.Join(", ", failed.Select(f => $"{f.Gesture} ({f.Action})")));
        }
    }
}
