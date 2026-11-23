using Grably.Core.Geometry;
using Grably.Core.Hotkeys;
using Grably.Core.Settings;

namespace Grably.Core.Recording;

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
}

/// <summary>A click ring at one moment: where (screen pixels), how far it has grown (0 to 1), which button.</summary>
public sealed record Ripple(PixelPoint Position, double Progress, MouseButtonKind Button);

/// <summary>The keystroke caption at one moment, with its opacity (it fades out at the end).</summary>
public sealed record KeystrokeCaption(string Text, double Opacity);

/// <summary>
/// The clicks and key presses of a recording, on the recording clock, and what to draw for them at any moment.
/// Fed from the input hooks' thread, read by the compositor's: thread-safe. Old events are dropped as time passes.
/// </summary>
public sealed class EffectTimeline(KeystrokeMode keystrokes)
{
    public static readonly TimeSpan RippleLength = TimeSpan.FromMilliseconds(550);
    public static readonly TimeSpan CaptionLength = TimeSpan.FromMilliseconds(1600);
    public static readonly TimeSpan CaptionFade = TimeSpan.FromMilliseconds(300);

    /// <summary>Typed characters join one caption while they come within this of each other.</summary>
    private static readonly TimeSpan TypingGap = TimeSpan.FromMilliseconds(900);

    private const int MaxCaption = 40;

    private readonly object _lock = new();
    private readonly List<(PixelPoint Position, MouseButtonKind Button, TimeSpan At)> _clicks = [];
    private string? _caption;
    private bool _captionIsTyping;
    private TimeSpan _captionAt;

    public KeystrokeMode Keystrokes { get; } = keystrokes;

    public void AddClick(PixelPoint position, MouseButtonKind button, TimeSpan at)
    {
        lock (_lock)
        {
            _clicks.Add((position, button, at));
            _clicks.RemoveAll(c => at - c.At > RippleLength);
        }
    }

    /// <summary>A key went down. Modifiers alone, and anything the keystroke mode does not show, are ignored.</summary>
    /// <param name="layout">What the user's keyboard layout makes of the key (decides what counts as typing).</param>
    public void AddKey(int virtualKey, HotkeyModifiers modifiers, TimeSpan at, KeyText layout = default)
    {
        if (KeystrokeFormatter.Format(virtualKey, modifiers, Keystrokes, layout) is not { } text)
        {
            return;
        }

        lock (_lock)
        {
            // Plain typing in "all keys" mode builds a word instead of flashing one letter at a time.
            var typing = text.Length <= 2 && (layout.AltGr || modifiers is HotkeyModifiers.None or HotkeyModifiers.Shift);
            if (typing && _captionIsTyping && _caption is not null && at - _captionAt <= TypingGap)
            {
                _caption = (_caption + text)[^Math.Min(MaxCaption, _caption.Length + 1)..];
            }
            else
            {
                _caption = text;
            }

            _captionIsTyping = typing;
            _captionAt = at;
        }
    }

    /// <summary>The click rings still growing at <paramref name="now"/>.</summary>
    public IReadOnlyList<Ripple> Ripples(TimeSpan now)
    {
        lock (_lock)
        {
            return _clicks
                .Where(c => now >= c.At && now - c.At < RippleLength)
                .Select(c => new Ripple(c.Position, (now - c.At) / RippleLength, c.Button))
                .ToList();
        }
    }

    /// <summary>The caption to show at <paramref name="now"/>, or null.</summary>
    public KeystrokeCaption? Caption(TimeSpan now)
    {
        lock (_lock)
        {
            if (_caption is null || now < _captionAt)
            {
                return null;
            }

            var age = now - _captionAt;
            if (age >= CaptionLength)
            {
                return null;
            }

            var fadeStart = CaptionLength - CaptionFade;
            var opacity = age <= fadeStart ? 1 : 1 - ((age - fadeStart) / CaptionFade);
            return new KeystrokeCaption(_caption, opacity);
        }
    }
}

/// <summary>What the active keyboard layout makes of a key press (worked out where the key was pressed).</summary>
/// <param name="Typed">The text the key types with the modifiers held, or null when it types nothing (or it is unknown).</param>
/// <param name="AltGr">Right Alt is held. On many layouts it types characters (AltGr+Q is @ on German keyboards), so a
/// key pressed with it is typing, never a shortcut, even though Windows reports it as Ctrl+Alt.</param>
public readonly record struct KeyText(string? Typed, bool AltGr);

/// <summary>What the keystroke caption says for a key press.</summary>
public static class KeystrokeFormatter
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;

    /// <summary>
    /// "Ctrl+Shift+S" for a shortcut. <see cref="KeystrokeMode.ShortcutsOnly"/> must never show anything typed:
    /// only Ctrl, Alt or Win combinations are shown, plus keys that never type (F1 to F24, Esc, arrows, Delete...);
    /// anything that types text on the user's layout, anything with AltGr, numpad keys and Alt codes (Alt plus numpad
    /// digits) are left out. <see cref="KeystrokeMode.AllKeys"/> shows typed text as typed. Null when nothing is shown.
    /// </summary>
    public static string? Format(int virtualKey, HotkeyModifiers modifiers, KeystrokeMode mode, KeyText text = default)
    {
        if (mode == KeystrokeMode.Off || virtualKey is VkShift or VkControl or VkMenu or VkLeftWin or VkRightWin
            || virtualKey is (>= 0xA0 and <= 0xA5)) // left/right Shift, Ctrl, Alt
        {
            return null;
        }

        var ctrlOrWin = (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Windows)) != 0;
        var alt = (modifiers & HotkeyModifiers.Alt) != 0;
        var typed = Printable(text.Typed);
        var typesText = typed is not null || UsTypingKey(virtualKey) is not null || IsNumpad(virtualKey) || IsOem(virtualKey)
            || virtualKey is 0x20 or 0x0D or 0x09 or 0x08; // Space, Enter, Tab, Backspace

        if (mode == KeystrokeMode.ShortcutsOnly)
        {
            if (text.AltGr || (alt && !ctrlOrWin && IsNumpad(virtualKey)))
            {
                return null; // AltGr types characters; Alt plus numpad digits types an Alt code
            }

            return !ctrlOrWin && !alt && typesText ? null : new HotkeyGesture(modifiers, virtualKey).ToString();
        }

        // All keys: text as typed (AltGr included), shortcuts by name.
        if (typed is not null && (text.AltGr || (!ctrlOrWin && !alt)))
        {
            return typed;
        }

        if (!ctrlOrWin && !alt && UsTypingKey(virtualKey) is { } character)
        {
            return (modifiers & HotkeyModifiers.Shift) != 0 ? character.ToUpperInvariant() : character.ToLowerInvariant();
        }

        return new HotkeyGesture(modifiers, virtualKey).ToString();
    }

    /// <summary>The text if it is visible characters; control characters (what Ctrl+C types) do not count.</summary>
    private static string? Printable(string? text) =>
        string.IsNullOrEmpty(text) || text.Any(c => c < 0x20 || c == 0x7F) ? null : text;

    private static bool IsNumpad(int vk) => vk is >= 0x60 and <= 0x6F; // digits and operators

    /// <summary>The keys whose characters depend on the layout: punctuation, accented letters, the European &lt;&gt; key.</summary>
    private static bool IsOem(int vk) => vk is (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF) or (>= 0xE1 and <= 0xE4);

    /// <summary>
    /// A letter, digit or common punctuation key as text on a US layout: the fallback when the layout's own answer is
    /// not known, and the safe assumption that these keys type.
    /// </summary>
    private static string? UsTypingKey(int vk) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)vk).ToString(), // A-Z
        >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 0-9
        >= 0x60 and <= 0x69 => ((char)('0' + vk - 0x60)).ToString(), // numpad digits
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => null,
    };
}
