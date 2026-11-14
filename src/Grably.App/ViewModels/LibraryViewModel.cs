using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grably.App.Shell;
using Grably.Core.Hotkeys;
using Grably.Core.Settings;

namespace Grably.App.ViewModels;

/// <summary>The Library main window. Captures arrive in M4; until then it shows the empty state.</summary>
public sealed partial class LibraryViewModel(AppCommands commands, SettingsStore settings) : ObservableObject
{
    public string EmptyTitle => "No captures yet";

    public string EmptyHint
    {
        get
        {
            var region = Describe(HotkeyAction.CaptureRegion);
            var record = Describe(HotkeyAction.ToggleRecording);
            return $"Press {region} to capture part of the screen, or {record} to start a recording. "
                + "Everything you capture shows up here.";
        }
    }

    [RelayCommand]
    private void CaptureRegion() => commands.CaptureRegion();

    [RelayCommand]
    private void Record() => commands.ToggleRecording();

    [RelayCommand]
    private void OpenSettings() => commands.OpenSettings();

    private string Describe(HotkeyAction action) =>
        settings.Current.Hotkeys.TryGetValue(action, out var gesture) && !gesture.IsNone
            ? gesture.ToString()
            : "the tray menu";
}
