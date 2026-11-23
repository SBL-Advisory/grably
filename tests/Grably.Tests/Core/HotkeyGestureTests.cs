using Grably.Core.Hotkeys;

namespace Grably.Tests.Core;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("PrintScreen", HotkeyModifiers.None, 0x2C)]
    [InlineData("Ctrl+PrintScreen", HotkeyModifiers.Control, 0x2C)]
    [InlineData("Ctrl+Shift+PrintScreen", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x2C)]
    [InlineData("Ctrl+Alt+T", HotkeyModifiers.Control | HotkeyModifiers.Alt, 'T')]
    [InlineData("Win+Shift+S", HotkeyModifiers.Windows | HotkeyModifiers.Shift, 'S')]
    [InlineData("Alt+F24", HotkeyModifiers.Alt, 0x87)]
    [InlineData("Ctrl+Num5", HotkeyModifiers.Control, 0x65)]
    public void Parses_gestures(string text, HotkeyModifiers modifiers, int key)
    {
        var gesture = HotkeyGesture.Parse(text);

        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(key, gesture.VirtualKey);
    }

    [Theory]
    [InlineData("ctrl + shift + prtsc", "Ctrl+Shift+PrintScreen")]
    [InlineData("Shift+Ctrl+A", "Ctrl+Shift+A")]
    [InlineData("control+escape", "Ctrl+Esc")]
    [InlineData("Alt+0x2C", "Alt+PrintScreen")]
    public void Formats_in_canonical_order(string text, string expected) =>
        Assert.Equal(expected, HotkeyGesture.Parse(text).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_means_no_hotkey(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.True(gesture.IsNone);
        Assert.Equal(string.Empty, gesture.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("A+B")]
    [InlineData("A+Ctrl")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+0xFFFF")]
    public void Rejects_invalid_text(string? text) =>
        Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Every_named_key_round_trips()
    {
        for (var code = 1; code < 0xFF; code++)
        {
            var name = KeyNames.GetName(code);
            if (name.StartsWith("0x", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(KeyNames.TryGetCode(name, out var parsed), name);
            Assert.Equal(code, parsed);
        }
    }

    [Theory]
    [InlineData("PrintScreen", true)]
    [InlineData("Shift+PrintScreen", true)]
    [InlineData("Ctrl+PrintScreen", true)]
    [InlineData("Pause", true)]
    [InlineData("F13", true)]
    [InlineData("F24", true)]
    [InlineData("Ctrl+Alt+T", true)]
    [InlineData("Ctrl+Shift+S", true)]
    [InlineData("Alt+Shift+X", true)]
    [InlineData("Win+Shift+S", true)]
    [InlineData("", true)]
    [InlineData("S", false)]
    [InlineData("Shift+S", false)]
    [InlineData("Space", false)]
    [InlineData("F5", false)]
    [InlineData("Shift+F12", false)]
    [InlineData("Ctrl+C", false)]
    [InlineData("Ctrl+Z", false)]
    [InlineData("Alt+S", false)]
    [InlineData("Alt+F4", false)]
    public void Only_combinations_that_leave_typing_alone_are_safe_as_global_hotkeys(string text, bool safe) =>
        Assert.Equal(safe, HotkeyGesture.Parse(text).IsSafeAsGlobalHotkey);
}
