using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Grably.App.Shell;
using Grably.Core;
using Grably.Core.Hotkeys;
using Grably.Core.Settings;
using Grably.Platform.Hotkeys;
using Grably.Platform.Shell;
using Microsoft.Extensions.Logging;

namespace Grably.App.ViewModels;

public sealed record Option<T>(T Value, string Label);

/// <summary>
/// The settings window works on a copy of the settings; Save writes it, applies the theme and start-with-Windows,
/// and re-registers hotkeys, reporting any that another app already owns.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly (HotkeyAction Action, string Label)[] HotkeyLabels =
    [
        (HotkeyAction.CaptureRegion, "Capture a region"),
        (HotkeyAction.CaptureActiveWindow, "Capture the active window"),
        (HotkeyAction.CaptureFullScreen, "Capture the full screen"),
        (HotkeyAction.ScrollingCapture, "Scrolling capture"),
        (HotkeyAction.CaptureText, "Capture text"),
        (HotkeyAction.ToggleRecording, "Start or stop recording"),
        (HotkeyAction.PauseRecording, "Pause or resume recording"),
    ];

    private readonly SettingsStore _store;
    private readonly HotkeyController _hotkeys;
    private readonly ThemeController _theme;
    private readonly AppPaths _paths;
    private readonly StartupRegistration _startup;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _startedWithWindows;
    private AppSettings _shown;

    public SettingsViewModel(
        SettingsStore store, HotkeyController hotkeys, ThemeController theme, AppPaths paths, StartupRegistration startup,
        ILogger<SettingsViewModel> logger)
    {
        _store = store;
        _hotkeys = hotkeys;
        _theme = theme;
        _paths = paths;
        _startup = startup;
        _logger = logger;

        Draft = store.Current.Clone();
        _startedWithWindows = startup.IsEnabled; // the registry is the truth (the user may have changed it elsewhere)
        Draft.General.StartWithWindows = _startedWithWindows;
        foreach (var (action, label) in HotkeyLabels)
        {
            Hotkeys.Add(new HotkeyRowViewModel(action, label, Draft.Hotkeys[action], gesture => Draft.Hotkeys[action] = gesture));
        }

        ShowStatus(hotkeys.Registrations);
        ScreenshotsFolder = Draft.General.ScreenshotsFolder ?? paths.DefaultScreenshotsFolder;
        VideosFolder = Draft.General.VideosFolder ?? paths.DefaultVideosFolder;
        _shown = Draft.Clone();
        if (Delays.All(d => d.Value != Draft.Screenshot.DelaySeconds))
        {
            Delays = [.. Delays, new(Draft.Screenshot.DelaySeconds, $"{Draft.Screenshot.DelaySeconds} seconds")];
        }
    }

    public AppSettings Draft { get; }

    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; } = [];

    public IReadOnlyList<Option<AppTheme>> Themes { get; } =
        [new(AppTheme.System, "Same as Windows"), new(AppTheme.Light, "Light"), new(AppTheme.Dark, "Dark")];

    public IReadOnlyList<Option<AfterCaptureAction>> AfterCaptureActions { get; } =
    [
        new(AfterCaptureAction.OpenEditor, "Open the editor"),
        new(AfterCaptureAction.ShowPreview, "Show a preview in the corner"),
        new(AfterCaptureAction.CopyAndSave, "Only copy and save"),
    ];

    public IReadOnlyList<Option<ImageFormat>> Formats { get; } = [new(ImageFormat.Png, "PNG (sharp, larger)"), new(ImageFormat.Jpeg, "JPEG (smaller)")];

    public IReadOnlyList<Option<int>> FrameRates { get; } = [new(15, "15 frames per second"), new(30, "30 frames per second"), new(60, "60 frames per second")];

    public IReadOnlyList<Option<VideoQuality>> Qualities { get; } =
        [new(VideoQuality.High, "High (larger files)"), new(VideoQuality.Standard, "Standard (smaller files)")];

    public IReadOnlyList<Option<int?>> MaxHeights { get; } =
        [new(null, "Same as the screen"), new(1440, "At most 1440p"), new(1080, "At most 1080p"), new(720, "At most 720p")];

    /// <summary>"Windows default" (followed when it changes) and the connected microphones.</summary>
    public IReadOnlyList<Option<string?>> Microphones { get; } =
        [new(null, "Windows default"), .. Grably.Platform.Recording.AudioDevices.Microphones().Select(d => new Option<string?>(d.Id, d.Name))];

    public IReadOnlyList<Option<int>> Delays { get; private set; } =
        [new(0, "No delay"), new(3, "3 seconds"), new(5, "5 seconds"), new(10, "10 seconds")];

    public string Version => AppInfo.Version;

    public string DataFolder => _paths.LocalRoot;

    [ObservableProperty]
    public partial string ScreenshotsFolder { get; set; }

    [ObservableProperty]
    public partial string VideosFolder { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>A PrintScreen hotkey is taken, almost always by Windows' own "Print screen opens Snipping Tool".</summary>
    [ObservableProperty]
    public partial bool ShowPrintScreenHint { get; set; }

    public ImageFormat Format
    {
        get => Draft.Screenshot.Format;
        set
        {
            Draft.Screenshot.Format = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsJpeg));
        }
    }

    public bool IsJpeg => Draft.Screenshot.Format == ImageFormat.Jpeg;

    public void SetScreenshotsFolder(string? folder)
    {
        Draft.General.ScreenshotsFolder = folder;
        ScreenshotsFolder = folder ?? _paths.DefaultScreenshotsFolder;
    }

    public void SetVideosFolder(string? folder)
    {
        Draft.General.VideosFolder = folder;
        VideosFolder = folder ?? _paths.DefaultVideosFolder;
    }

    /// <summary>Saves and applies everything. Returns false (and explains) when something needs the user's attention.</summary>
    public bool Save()
    {
        ErrorMessage = null;
        if (Draft.FindDuplicateHotkey() is { } duplicate)
        {
            ErrorMessage = $"{duplicate} is used for more than one action. Give each action its own keys.";
            return false;
        }

        try
        {
            _store.Save(_store.Current.WithChangesFrom(_shown, Draft));
            _shown = Draft.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save settings");
            ErrorMessage = $"The settings could not be saved: {ex.Message}";
            return false;
        }

        _theme.ApplyPreference(Draft.General.Theme);
        try
        {
            // Only touch the Run key when the choice changed, so saving from a development build never repoints
            // an installed Grably's entry at itself.
            if (Draft.General.StartWithWindows != _startedWithWindows && Environment.ProcessPath is { } exe)
            {
                _startup.Set(Draft.General.StartWithWindows, exe);
                _startedWithWindows = Draft.General.StartWithWindows;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not change start with Windows");
            ErrorMessage = "Starting with Windows could not be changed. Your other settings were saved.";
            return false;
        }

        ShowStatus(_hotkeys.ApplyNow());
        if (Hotkeys.Any(h => h.IsTaken))
        {
            ErrorMessage = "Some keys are already used by another app or by Windows. Pick other keys for them.";
            return false;
        }

        return true;
    }

    public void SuspendHotkeys() => _hotkeys.Suspend();

    public void ResumeHotkeys() => _hotkeys.Resume();

    private void ShowStatus(IReadOnlyList<HotkeyRegistration> registrations)
    {
        foreach (var row in Hotkeys)
        {
            var registration = registrations.FirstOrDefault(r => r.Action == row.Action);
            row.IsTaken = registration is { Succeeded: false } && registration.Gesture == row.Gesture;
        }

        ShowPrintScreenHint = Hotkeys.Any(h => h.IsTaken && h.Gesture.VirtualKey == 0x2C);
    }
}

/// <summary>One hotkey in the settings list.</summary>
public sealed partial class HotkeyRowViewModel(HotkeyAction action, string label, HotkeyGesture gesture, Action<HotkeyGesture> changed)
    : ObservableObject
{
    public HotkeyAction Action { get; } = action;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial HotkeyGesture Gesture { get; set; } = gesture;

    /// <summary>Another app or Windows owns this combination, so Grably could not register it.</summary>
    [ObservableProperty]
    public partial bool IsTaken { get; set; }

    partial void OnGestureChanged(HotkeyGesture value)
    {
        IsTaken = false;
        changed(value);
    }
}
