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
    /// A copy of these settings with the sections the settings window edits (general, hotkeys, screenshots) taken
    /// from <paramref name="edited"/>. Everything else, such as the editor's remembered tool styles, stays as it is
    /// now, so saving the window never undoes changes made elsewhere while it was open.
    /// </summary>
    public AppSettings WithWindowSectionsFrom(AppSettings edited)
    {
        var result = Clone();
        var source = edited.Clone();
        result.General = source.General;
        result.Hotkeys = source.Hotkeys;
        result.Screenshot = source.Screenshot;
        return result;
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
