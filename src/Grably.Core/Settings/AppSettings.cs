using Grably.Core.Annotations;
using Grably.Core.Hotkeys;

namespace Grably.Core.Settings;

/// <summary>
/// Everything the user can configure. Serialised to settings.json; new properties must have defaults so older
/// files keep loading. Bump <see cref="CurrentSchemaVersion"/> and add a step to <see cref="SettingsMigrations"/>
/// only when an existing property changes meaning or shape.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GeneralSettings General { get; set; } = new();

    public Dictionary<HotkeyAction, HotkeyGesture> Hotkeys { get; set; } = DefaultHotkeys();

    public ScreenshotSettings Screenshot { get; set; } = new();

    public RecordingSettings Recording { get; set; } = new();

    public EditorSettings Editor { get; set; } = new();

    public static Dictionary<HotkeyAction, HotkeyGesture> DefaultHotkeys() => new()
    {
        [HotkeyAction.CaptureRegion] = HotkeyGesture.Parse("PrintScreen"),
        [HotkeyAction.CaptureFullScreen] = HotkeyGesture.Parse("Ctrl+PrintScreen"),
        [HotkeyAction.CaptureActiveWindow] = HotkeyGesture.Parse("Alt+PrintScreen"),
        [HotkeyAction.ScrollingCapture] = HotkeyGesture.Parse("Ctrl+Shift+PrintScreen"),
        [HotkeyAction.CaptureText] = HotkeyGesture.Parse("Ctrl+Alt+T"),
        [HotkeyAction.ToggleRecording] = HotkeyGesture.Parse("Shift+PrintScreen"),
        [HotkeyAction.PauseRecording] = HotkeyGesture.Parse("Ctrl+Alt+P"),
    };

    /// <summary>
    /// A copy of these settings with what the settings window changed (from <paramref name="original"/>, what it
    /// showed, to <paramref name="edited"/>) applied. Values it left alone keep their current value, so saving the
    /// window never undoes changes made elsewhere while it was open: editor tool styles, or the sound and frame-rate
    /// choices of the pre-record bar.
    /// </summary>
    public AppSettings WithChangesFrom(AppSettings original, AppSettings edited)
    {
        var result = Clone();
        var after = edited.Clone();
        CopyChanged(original.General, after.General, result.General);
        CopyChanged(original.Screenshot, after.Screenshot, result.Screenshot);
        CopyChanged(original.Recording, after.Recording, result.Recording);
        foreach (var (action, gesture) in after.Hotkeys)
        {
            if (!original.Hotkeys.TryGetValue(action, out var before) || before != gesture)
            {
                result.Hotkeys[action] = gesture;
            }
        }

        return result;
    }

    private static void CopyChanged<T>(T original, T edited, T target)
        where T : class
    {
        foreach (var property in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite))
        {
            var value = property.GetValue(edited);
            if (!Equals(property.GetValue(original), value))
            {
                property.SetValue(target, value);
            }
        }
    }

    /// <summary>A key combination given to more than one action, or null when every hotkey is unique.</summary>
    public HotkeyGesture? FindDuplicateHotkey() =>
        Hotkeys.Values.Where(g => !g.IsNone).GroupBy(g => g).FirstOrDefault(g => g.Count() > 1)?.Key;

    /// <summary>A deep copy, for editing in the settings window without touching the live settings until Save.</summary>
    public AppSettings Clone()
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
        copy.Normalize();
        return copy;
    }

    /// <summary>Fills anything missing or null after loading an older or hand-edited file.</summary>
    public void Normalize()
    {
        General ??= new GeneralSettings();
        Screenshot ??= new ScreenshotSettings();
        Recording ??= new RecordingSettings();
        Editor ??= new EditorSettings();
        Editor.ToolStyles ??= [];
        Hotkeys ??= [];
        foreach (var (action, gesture) in DefaultHotkeys())
        {
            Hotkeys.TryAdd(action, gesture);
        }

        Screenshot.JpegQuality = Math.Clamp(Screenshot.JpegQuality, 10, 100);
        Screenshot.DelaySeconds = Math.Clamp(Screenshot.DelaySeconds, 0, 60);
        Recording.FrameRate = Recording.FrameRate is 15 or 30 or 60 ? Recording.FrameRate : 30;
    }
}

public sealed class GeneralSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool StartWithWindows { get; set; }

    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Null means the default (Pictures\Grably).</summary>
    public string? ScreenshotsFolder { get; set; }

    /// <summary>Null means the default (Videos\Grably).</summary>
    public string? VideosFolder { get; set; }
}

public sealed class EditorSettings
{
    /// <summary>The last style used with each editor tool, so the next annotation looks like the previous one.</summary>
    public Dictionary<EditorTool, AnnotationStyle> ToolStyles { get; set; } = [];
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class ScreenshotSettings
{
    public AfterCaptureAction AfterCapture { get; set; } = AfterCaptureAction.OpenEditor;

    public bool CopyToClipboard { get; set; } = true;

    public ImageFormat Format { get; set; } = ImageFormat.Png;

    public int JpegQuality { get; set; } = 90;

    public bool IncludeCursor { get; set; }

    public int DelaySeconds { get; set; }

    /// <summary>
    /// After dragging a region, keep it adjustable (handles, arrow keys) until Enter or a double-click,
    /// instead of capturing as soon as the mouse button is released.
    /// </summary>
    public bool AdjustBeforeCapture { get; set; }

    /// <summary>Read the text in screenshots (on this PC) so library search finds words inside them.</summary>
    public bool MakeTextSearchable { get; set; } = true;
}

public enum AfterCaptureAction
{
    /// <summary>Open the annotation editor (the image is already copied and saved).</summary>
    OpenEditor,

    /// <summary>Show a small preview in the corner of the screen with quick actions.</summary>
    ShowPreview,

    /// <summary>Only copy and save, with a notification.</summary>
    CopyAndSave,
}

public enum ImageFormat
{
    Png,
    Jpeg,
}

public sealed class RecordingSettings
{
    /// <summary>15, 30 or 60.</summary>
    public int FrameRate { get; set; } = 30;

    /// <summary>Downscale taller captures to this height; null keeps the source resolution.</summary>
    public int? MaxHeight { get; set; }

    public VideoQuality Quality { get; set; } = VideoQuality.High;

    public bool RecordSystemAudio { get; set; } = true;

    /// <summary>Null means the default output device.</summary>
    public string? SystemAudioDeviceId { get; set; }

    public bool RecordMicrophone { get; set; }

    /// <summary>Null means the default input device.</summary>
    public string? MicrophoneDeviceId { get; set; }

    public bool ShowCountdown { get; set; } = true;

    /// <summary>The pre-record bar's Effects switch: draw the highlight, clicks and keys chosen below.</summary>
    public bool ShowEffects { get; set; } = true;

    public bool HighlightCursor { get; set; } = true;

    public bool ShowClicks { get; set; } = true;

    public KeystrokeMode Keystrokes { get; set; } = KeystrokeMode.Off;

    public bool WebcamEnabled { get; set; }

    public string? WebcamDeviceId { get; set; }

    public WebcamShape WebcamShape { get; set; } = WebcamShape.Circle;

    public WebcamSize WebcamSize { get; set; } = WebcamSize.Medium;
}

public enum VideoQuality
{
    Standard,
    High,
}

public enum KeystrokeMode
{
    Off,

    /// <summary>Only combinations with Ctrl, Alt or Win, so typed text is never shown.</summary>
    ShortcutsOnly,

    AllKeys,
}

public enum WebcamShape
{
    Circle,
    RoundedRectangle,
}

public enum WebcamSize
{
    Small,
    Medium,
    Large,
}
