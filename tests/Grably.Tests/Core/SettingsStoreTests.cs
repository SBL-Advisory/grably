using Grably.Core.Annotations;
using Grably.Core.Hotkeys;
using Grably.Core.Settings;

namespace Grably.Tests.Core;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_folder);

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Missing_file_gives_defaults_without_writing_anything()
    {
        var settings = new SettingsStore(SettingsPath).Current;

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(AfterCaptureAction.OpenEditor, settings.Screenshot.AfterCapture);
        Assert.Equal(HotkeyGesture.Parse("PrintScreen"), settings.Hotkeys[HotkeyAction.CaptureRegion]);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Saved_settings_load_back_in_a_new_store()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = store.Current;
        settings.General.Theme = AppTheme.Dark;
        settings.Recording.FrameRate = 60;
        settings.Recording.Keystrokes = KeystrokeMode.ShortcutsOnly;
        settings.Hotkeys[HotkeyAction.CaptureText] = HotkeyGesture.Parse("Ctrl+Shift+T");
        settings.Hotkeys[HotkeyAction.ScrollingCapture] = HotkeyGesture.None;
        store.Save(settings);

        var reloaded = new SettingsStore(SettingsPath).Current;

        Assert.Equal(AppTheme.Dark, reloaded.General.Theme);
        Assert.Equal(60, reloaded.Recording.FrameRate);
        Assert.Equal(KeystrokeMode.ShortcutsOnly, reloaded.Recording.Keystrokes);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+Shift+T"), reloaded.Hotkeys[HotkeyAction.CaptureText]);
        Assert.True(reloaded.Hotkeys[HotkeyAction.ScrollingCapture].IsNone);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void File_is_readable_json_with_camel_case_names_and_hotkey_strings()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(store.Current);

        var json = File.ReadAllText(SettingsPath);

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"captureRegion\": \"PrintScreen\"", json, StringComparison.Ordinal);
        Assert.Contains("\"afterCapture\": \"openEditor\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Partial_file_is_completed_with_defaults()
    {
        File.WriteAllText(SettingsPath, """
            {
              // hand-edited: comments and trailing commas are tolerated
              "schemaVersion": 1,
              "general": { "theme": "light", },
              "hotkeys": { "captureRegion": "Ctrl+Alt+R" }
            }
            """);

        var settings = new SettingsStore(SettingsPath).Current;

        Assert.Equal(AppTheme.Light, settings.General.Theme);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+Alt+R"), settings.Hotkeys[HotkeyAction.CaptureRegion]);
        Assert.Equal(HotkeyGesture.Parse("Shift+PrintScreen"), settings.Hotkeys[HotkeyAction.ToggleRecording]);
        Assert.NotNull(settings.Recording);
        Assert.Equal(30, settings.Recording.FrameRate);
    }

    [Fact]
    public void Out_of_range_values_are_corrected()
    {
        File.WriteAllText(SettingsPath, """
            { "screenshot": { "jpegQuality": 500, "delaySeconds": -3 }, "recording": { "frameRate": 24 } }
            """);

        var settings = new SettingsStore(SettingsPath).Current;

        Assert.Equal(100, settings.Screenshot.JpegQuality);
        Assert.Equal(0, settings.Screenshot.DelaySeconds);
        Assert.Equal(30, settings.Recording.FrameRate);
    }

    [Fact]
    public void Invalid_values_reset_only_themselves()
    {
        File.WriteAllText(SettingsPath, """
            {
              "general": { "theme": "purple", "checkForUpdates": false },
              "hotkeys": {
                "captureRegion": "Ctrl+NotAKey",
                "captureText": "Ctrl+Shift+T",
                "captureFullScreen": null,
                "captureGif": "Ctrl+G"
              },
              "screenshot": "not an object",
              "recording": { "frameRate": "fast", "keystrokes": "shortcutsOnly", "webcamShape": "hexagon" }
            }
            """);

        var settings = new SettingsStore(SettingsPath).Current;

        Assert.Equal(AppTheme.System, settings.General.Theme);              // invalid: default
        Assert.False(settings.General.CheckForUpdates);                      // valid sibling: kept
        Assert.Equal(HotkeyGesture.Parse("PrintScreen"), settings.Hotkeys[HotkeyAction.CaptureRegion]);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+Shift+T"), settings.Hotkeys[HotkeyAction.CaptureText]);
        Assert.True(settings.Hotkeys[HotkeyAction.CaptureFullScreen].IsNone); // null: turned off
        Assert.Equal(AfterCaptureAction.OpenEditor, settings.Screenshot.AfterCapture);
        Assert.Equal(30, settings.Recording.FrameRate);
        Assert.Equal(KeystrokeMode.ShortcutsOnly, settings.Recording.Keystrokes);
        Assert.Equal(WebcamShape.Circle, settings.Recording.WebcamShape);
        Assert.True(File.Exists(SettingsPath));                              // nothing was moved aside
    }

    [Fact]
    public void Null_keeps_the_default_except_for_hotkeys_where_it_means_off()
    {
        File.WriteAllText(SettingsPath, """
            {
              "general": { "checkForUpdates": null, "screenshotsFolder": null },
              "screenshot": { "jpegQuality": null, "copyToClipboard": null },
              "recording": { "quality": null, "maxHeight": null, "recordSystemAudio": null },
              "hotkeys": { "captureText": null }
            }
            """);

        var settings = new SettingsStore(SettingsPath).Current;

        Assert.True(settings.General.CheckForUpdates);
        Assert.Null(settings.General.ScreenshotsFolder);
        Assert.Equal(90, settings.Screenshot.JpegQuality);
        Assert.True(settings.Screenshot.CopyToClipboard);
        Assert.Equal(VideoQuality.High, settings.Recording.Quality);
        Assert.Null(settings.Recording.MaxHeight);
        Assert.True(settings.Recording.RecordSystemAudio);
        Assert.True(settings.Hotkeys[HotkeyAction.CaptureText].IsNone);
    }

    [Fact]
    public void Numbers_are_not_accepted_as_enum_names()
    {
        File.WriteAllText(SettingsPath, """
            { "general": { "theme": 2 }, "screenshot": { "format": "7" }, "hotkeys": { "42": "Ctrl+G", "1": "Ctrl+H" } }
            """);

        var settings = new SettingsStore(SettingsPath).Current;

        Assert.Equal(AppTheme.System, settings.General.Theme);
        Assert.Equal(ImageFormat.Png, settings.Screenshot.Format);
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, settings.Hotkeys.Count);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+PrintScreen"), settings.Hotkeys[HotkeyAction.CaptureFullScreen]);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_left_in_place()
    {
        File.WriteAllText(SettingsPath, """{ "general": { "theme": "dark" } }""");
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var settings = new SettingsStore(SettingsPath).Current;

            Assert.Equal(AppTheme.System, settings.General.Theme);
        }

        Assert.Empty(Directory.GetFiles(_folder, "settings.json.broken-*.json"));
        Assert.Equal(AppTheme.Dark, new SettingsStore(SettingsPath).Current.General.Theme);
    }

    [Fact]
    public void Settings_from_a_newer_build_keep_everything_this_build_understands()
    {
        File.WriteAllText(SettingsPath, """
            {
              "schemaVersion": 2,
              "general": { "theme": "dark" },
              "screenshot": { "afterCapture": "uploadToCloud", "format": "jpeg" },
              "hotkeys": { "recordGif": "Ctrl+Alt+G", "captureText": "Ctrl+Alt+X" }
            }
            """);

        var settings = new SettingsStore(SettingsPath).Current;

        Assert.Equal(AppTheme.Dark, settings.General.Theme);
        Assert.Equal(AfterCaptureAction.OpenEditor, settings.Screenshot.AfterCapture);
        Assert.Equal(ImageFormat.Jpeg, settings.Screenshot.Format);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+Alt+X"), settings.Hotkeys[HotkeyAction.CaptureText]);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "general": { "theme": "dark" }, "general": { "theme": "light" } }""")]
    public void Unreadable_file_is_kept_aside_and_defaults_are_used(string content)
    {
        File.WriteAllText(SettingsPath, content);

        var settings = new SettingsStore(SettingsPath).Current;

        Assert.Equal(AppTheme.System, settings.General.Theme);
        Assert.False(File.Exists(SettingsPath));
        var broken = Assert.Single(Directory.GetFiles(_folder, "settings.json.broken-*.json"));
        Assert.Equal(content, File.ReadAllText(broken));
    }

    [Fact]
    public void Newer_schema_still_loads_known_settings()
    {
        File.WriteAllText(SettingsPath, """
            { "schemaVersion": 99, "general": { "theme": "dark" }, "someFutureSection": { "x": 1 } }
            """);

        var settings = new SettingsStore(SettingsPath).Current;

        Assert.Equal(AppTheme.Dark, settings.General.Theme);
    }

    [Fact]
    public void Save_raises_Saved()
    {
        var store = new SettingsStore(SettingsPath);
        AppSettings? saved = null;
        store.Saved += (_, s) => saved = s;

        store.Save(store.Current);

        Assert.Same(store.Current, saved);
    }

    [Fact]
    public void A_clone_can_be_edited_without_touching_the_original()
    {
        var original = new AppSettings();
        original.General.ScreenshotsFolder = @"D:\Shots";

        var copy = original.Clone();
        copy.General.ScreenshotsFolder = @"E:\Elsewhere";
        copy.Screenshot.JpegQuality = 55;
        copy.Hotkeys[HotkeyAction.CaptureRegion] = HotkeyGesture.Parse("Ctrl+Shift+S");

        Assert.Equal(@"D:\Shots", original.General.ScreenshotsFolder);
        Assert.Equal(90, original.Screenshot.JpegQuality);
        Assert.Equal(HotkeyGesture.Parse("PrintScreen"), original.Hotkeys[HotkeyAction.CaptureRegion]);
        Assert.Equal(@"D:\Shots", original.Clone().General.ScreenshotsFolder);
    }

    [Fact]
    public void Saving_the_settings_window_applies_only_what_it_changed()
    {
        var current = new AppSettings();
        var shown = current.Clone(); // the window opens
        var draft = shown.Clone();
        current.Editor.ToolStyles[EditorTool.Arrow] = new AnnotationStyle { Thickness = 9 }; // an editor closes
        current.Recording.FrameRate = 60; // the pre-record bar remembers a choice
        current.Recording.RecordMicrophone = true;
        draft.General.Theme = AppTheme.Dark; // the user's edits in the window
        draft.Screenshot.AfterCapture = AfterCaptureAction.ShowPreview;
        draft.Recording.ShowCountdown = false;
        draft.Hotkeys[HotkeyAction.CaptureRegion] = HotkeyGesture.Parse("Ctrl+Shift+S");

        var saved = current.WithChangesFrom(shown, draft);

        Assert.Equal(AppTheme.Dark, saved.General.Theme);
        Assert.Equal(AfterCaptureAction.ShowPreview, saved.Screenshot.AfterCapture);
        Assert.False(saved.Recording.ShowCountdown);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+Shift+S"), saved.Hotkeys[HotkeyAction.CaptureRegion]);
        Assert.Equal(9, saved.Editor.ToolStyles[EditorTool.Arrow].Thickness);
        Assert.Equal(60, saved.Recording.FrameRate); // not touched in the window: the newer value stays
        Assert.True(saved.Recording.RecordMicrophone);
        Assert.Equal(HotkeyGesture.Parse("Ctrl+PrintScreen"), saved.Hotkeys[HotkeyAction.CaptureFullScreen]);

        draft.General.Theme = AppTheme.Light; // the window stays open after a failed save and keeps editing
        Assert.Equal(AppTheme.Dark, saved.General.Theme);
        Assert.NotSame(current.Editor, saved.Editor);
    }

    [Fact]
    public void Duplicate_hotkeys_are_found_and_turned_off_ones_never_clash()
    {
        var settings = new AppSettings();
        Assert.Null(settings.FindDuplicateHotkey());

        settings.Hotkeys[HotkeyAction.CaptureText] = HotkeyGesture.None;
        settings.Hotkeys[HotkeyAction.PauseRecording] = HotkeyGesture.None;
        Assert.Null(settings.FindDuplicateHotkey());

        settings.Hotkeys[HotkeyAction.CaptureFullScreen] = HotkeyGesture.Parse("PrintScreen");
        Assert.Equal(HotkeyGesture.Parse("PrintScreen"), settings.FindDuplicateHotkey());
    }
}
