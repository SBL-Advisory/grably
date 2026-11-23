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

    private Dictionary<HotkeyAction, HotkeyGesture>? _applied;
    private bool _suspended;

    /// <summary>
    /// Releases Grably's hotkeys (while the settings window records a new one, so pressing PrintScreen there
    /// does not start a capture). <see cref="Resume"/> registers them again.
    /// </summary>
    public void Suspend()
    {
        _suspended = true;
        _applied = null;
        hotkeys.Apply([]);
    }

    public void Resume()
    {
        _suspended = false;
        ApplyNow();
    }

    /// <summary>Registers the hotkeys from the current settings now and returns the results.</summary>
    public IReadOnlyList<HotkeyRegistration> ApplyNow()
    {
        ApplyFromSettings();
        return Registrations;
    }

    private void ApplyFromSettings()
    {
        if (_suspended)
        {
            return;
        }

        // Pause is only registered while recording, so it does not hold on to the key combination otherwise.
        var bindings = settings.Current.Hotkeys.Where(b => b.Key != HotkeyAction.PauseRecording)
            .ToDictionary(b => b.Key, b => b.Value);
        if (_applied is not null && _applied.Count == bindings.Count && !_applied.Except(bindings).Any()
            && Registrations.All(r => r.Succeeded))
        {
            return; // settings were saved for another reason and every hotkey is registered
        }

        _applied = bindings;
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
