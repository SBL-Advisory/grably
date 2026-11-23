using Grably.Core.Geometry;
using Grably.Core.Hotkeys;
using Grably.Core.Recording;
using Grably.Core.Settings;

namespace Grably.Tests.Core;

public class CursorEffectsTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Theory]
    [InlineData(0x43, HotkeyModifiers.Control, KeystrokeMode.ShortcutsOnly, "Ctrl+C")]
    [InlineData(0x53, HotkeyModifiers.Control | HotkeyModifiers.Shift, KeystrokeMode.ShortcutsOnly, "Ctrl+Shift+S")]
    [InlineData(0x74, HotkeyModifiers.None, KeystrokeMode.ShortcutsOnly, "F5")]
    [InlineData(0x1B, HotkeyModifiers.None, KeystrokeMode.ShortcutsOnly, "Esc")]
    [InlineData(0x41, HotkeyModifiers.None, KeystrokeMode.ShortcutsOnly, null)] // typing is never shown
    [InlineData(0x41, HotkeyModifiers.Shift, KeystrokeMode.ShortcutsOnly, null)]
    [InlineData(0x31, HotkeyModifiers.None, KeystrokeMode.ShortcutsOnly, null)]
    [InlineData(0x0D, HotkeyModifiers.None, KeystrokeMode.ShortcutsOnly, null)]
    [InlineData(0x20, HotkeyModifiers.None, KeystrokeMode.ShortcutsOnly, null)]
    [InlineData(0x41, HotkeyModifiers.None, KeystrokeMode.AllKeys, "a")]
    [InlineData(0x41, HotkeyModifiers.Shift, KeystrokeMode.AllKeys, "A")]
    [InlineData(0x0D, HotkeyModifiers.None, KeystrokeMode.AllKeys, "Enter")]
    [InlineData(0x43, HotkeyModifiers.Control, KeystrokeMode.Off, null)]
    [InlineData(0x11, HotkeyModifiers.Control, KeystrokeMode.AllKeys, null)] // a modifier alone
    [InlineData(0xA2, HotkeyModifiers.Control, KeystrokeMode.AllKeys, null)]
    public void Captions_show_shortcuts_and_only_show_typing_when_asked(int vk, HotkeyModifiers modifiers, KeystrokeMode mode, string? expected) =>
        Assert.Equal(expected, KeystrokeFormatter.Format(vk, modifiers, mode));

    [Theory]
    [InlineData(0x51, HotkeyModifiers.Control | HotkeyModifiers.Alt, "@", true)] // AltGr+Q on a German layout
    [InlineData(0x45, HotkeyModifiers.Control | HotkeyModifiers.Alt, "\u20AC", true)] // AltGr+E: euro sign
    [InlineData(0x37, HotkeyModifiers.Control | HotkeyModifiers.Alt, "{", true)] // AltGr+7
    [InlineData(0xE2, HotkeyModifiers.None, "<", false)] // the European <> key
    [InlineData(0xE2, HotkeyModifiers.Shift, ">", false)]
    [InlineData(0xE2, HotkeyModifiers.None, null, false)] // even when the layout's answer is unknown
    [InlineData(0x6B, HotkeyModifiers.None, "+", false)] // numpad plus
    [InlineData(0x6E, HotkeyModifiers.None, null, false)] // numpad decimal, answer unknown
    [InlineData(0x66, HotkeyModifiers.Alt, null, false)] // Alt+Num6: typing an Alt code
    [InlineData(0xDE, HotkeyModifiers.None, "\u00E4", false)] // a letter on its own key (German a-umlaut)
    [InlineData(0x41, HotkeyModifiers.None, "a", false)]
    public void Shortcuts_only_never_shows_text_typed_on_any_layout(int vk, HotkeyModifiers modifiers, string? typed, bool altGr) =>
        Assert.Null(KeystrokeFormatter.Format(vk, modifiers, KeystrokeMode.ShortcutsOnly, new KeyText(typed is null ? null : System.Text.RegularExpressions.Regex.Unescape(typed), altGr)));

    [Fact]
    public void Real_shortcuts_still_show_with_the_layout_known()
    {
        Assert.Equal("Ctrl+C", KeystrokeFormatter.Format(0x43, HotkeyModifiers.Control, KeystrokeMode.ShortcutsOnly, new KeyText("\u0003", false)));
        Assert.Equal("Alt+F4", KeystrokeFormatter.Format(0x73, HotkeyModifiers.Alt, KeystrokeMode.ShortcutsOnly, new KeyText(null, false)));
        Assert.Equal("@", KeystrokeFormatter.Format(0x51, HotkeyModifiers.Control | HotkeyModifiers.Alt, KeystrokeMode.AllKeys, new KeyText("@", true)));
        Assert.Equal("\u00E4", KeystrokeFormatter.Format(0xDE, HotkeyModifiers.None, KeystrokeMode.AllKeys, new KeyText("\u00E4", false)));
    }

    [Fact]
    public void A_click_ring_grows_and_disappears()
    {
        var timeline = new EffectTimeline(KeystrokeMode.Off);
        timeline.AddClick(new PixelPoint(100, 200), MouseButtonKind.Right, Ms(1000));

        Assert.Empty(timeline.Ripples(Ms(999)));
        var ripple = Assert.Single(timeline.Ripples(Ms(1000) + (EffectTimeline.RippleLength / 2)));
        Assert.Equal((new PixelPoint(100, 200), MouseButtonKind.Right), (ripple.Position, ripple.Button));
        Assert.Equal(0.5, ripple.Progress, 3);
        Assert.Empty(timeline.Ripples(Ms(1000) + EffectTimeline.RippleLength));
    }

    [Fact]
    public void A_caption_stays_then_fades_and_typed_letters_build_a_word()
    {
        var timeline = new EffectTimeline(KeystrokeMode.AllKeys);
        timeline.AddKey(0x48, HotkeyModifiers.Shift, Ms(0)); // H
        timeline.AddKey(0x49, HotkeyModifiers.None, Ms(200)); // i
        Assert.Equal(new KeystrokeCaption("Hi", 1), timeline.Caption(Ms(500)));

        var fading = timeline.Caption(Ms(200) + EffectTimeline.CaptionLength - (EffectTimeline.CaptionFade / 2))!;
        Assert.Equal(0.5, fading.Opacity, 3);
        Assert.Null(timeline.Caption(Ms(200) + EffectTimeline.CaptionLength));

        timeline.AddKey(0x53, HotkeyModifiers.Control, Ms(5000)); // a shortcut replaces the word
        timeline.AddKey(0x41, HotkeyModifiers.None, Ms(5100)); // and typing after it starts a new one
        Assert.Equal("a", timeline.Caption(Ms(5200))!.Text);
    }
}
