using System.Collections.Frozen;

namespace Grably.Core.Hotkeys;

/// <summary>Maps Win32 virtual-key codes to the names used in settings files and the UI, and back.</summary>
public static class KeyNames
{
    private static readonly FrozenDictionary<int, string> NamesByCode = BuildNames().ToFrozenDictionary();

    private static readonly FrozenDictionary<string, int> CodesByName = NamesByCode
        .ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase)
        .Concat(Aliases())
        .ToFrozenDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The display name of a virtual-key code, or "0x.." for codes without a name.</summary>
    public static string GetName(int virtualKey) =>
        NamesByCode.TryGetValue(virtualKey, out var name) ? name : $"0x{virtualKey:X2}";

    public static bool TryGetCode(string name, out int virtualKey)
    {
        if (CodesByName.TryGetValue(name, out virtualKey))
        {
            return true;
        }

        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out virtualKey)
            && virtualKey is > 0 and < 0xFF)
        {
            return true;
        }

        virtualKey = 0;
        return false;
    }

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [0x08] = "Backspace",
            [0x09] = "Tab",
            [0x0D] = "Enter",
            [0x13] = "Pause",
            [0x14] = "CapsLock",
            [0x1B] = "Esc",
            [0x20] = "Space",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2C] = "PrintScreen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x6A] = "NumMultiply",
            [0x6B] = "NumPlus",
            [0x6D] = "NumMinus",
            [0x6E] = "NumDecimal",
            [0x6F] = "NumDivide",
            [0x91] = "ScrollLock",
            [0xBA] = "Semicolon",
            [0xBB] = "Plus",
            [0xBC] = "Comma",
            [0xBD] = "Minus",
            [0xBE] = "Period",
            [0xBF] = "Slash",
            [0xC0] = "Backtick",
            [0xDB] = "OpenBracket",
            [0xDC] = "Backslash",
            [0xDD] = "CloseBracket",
            [0xDE] = "Quote",
        };
        for (var c = '0'; c <= '9'; c++)
        {
            names[c] = c.ToString();
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            names[c] = c.ToString();
        }

        for (var i = 0; i <= 9; i++)
        {
            names[0x60 + i] = $"Num{i}";
        }

        for (var i = 1; i <= 24; i++)
        {
            names[0x6F + i] = $"F{i}";
        }

        return names;
    }

    private static IEnumerable<KeyValuePair<string, int>> Aliases() =>
    [
        new("PrtSc", 0x2C),
        new("PrtScn", 0x2C),
        new("Snapshot", 0x2C),
        new("Escape", 0x1B),
        new("Return", 0x0D),
        new("Del", 0x2E),
        new("Ins", 0x2D),
        new("PgUp", 0x21),
        new("PgDn", 0x22),
    ];
}
