using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grably.Core.Hotkeys;

/// <summary>
/// A key combination such as Ctrl+Shift+PrintScreen. Stored in settings as its display string;
/// an empty string means the hotkey is turned off.
/// </summary>
[JsonConverter(typeof(HotkeyGestureJsonConverter))]
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static HotkeyGesture None => default;

    public bool IsNone => VirtualKey == 0;

    public static HotkeyGesture Parse(string text) =>
        TryParse(text, out var gesture) ? gesture : throw new FormatException($"Not a valid hotkey: '{text}'.");

    /// <summary>Parses "Ctrl+Alt+T". Empty or whitespace text parses to <see cref="None"/>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out HotkeyGesture gesture)
    {
        gesture = None;
        if (text is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var modifiers = HotkeyModifiers.None;
        var key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                return false;
            }

            var modifier = raw.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (modifier != HotkeyModifiers.None)
            {
                if (key != 0 || modifiers.HasFlag(modifier))
                {
                    return false; // modifiers must come first, once each
                }

                modifiers |= modifier;
                continue;
            }

            if (key != 0 || !KeyNames.TryGetCode(raw, out key))
            {
                return false; // a second key, or an unknown key name
            }
        }

        if (key == 0)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    public override string ToString()
    {
        if (IsNone)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyNames.GetName(VirtualKey));
        return string.Join('+', parts);
    }
}

internal sealed class HotkeyGestureJsonConverter : JsonConverter<HotkeyGesture>
{
    public override HotkeyGesture Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return HotkeyGesture.TryParse(text, out var gesture)
            ? gesture
            : throw new JsonException($"Not a valid hotkey: '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, HotkeyGesture value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
