using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grably.Core.Hotkeys;

namespace Grably.Core.Settings;

/// <summary>
/// Copies values from a settings JSON object onto a settings object one property at a time, so a single bad value
/// (an unknown theme name, a hotkey this build does not know, a future enum value) only resets that one setting to
/// its default instead of discarding the whole file. Unknown JSON properties are ignored.
/// </summary>
internal static class TolerantSettingsReader
{
    /// <summary>Applies <paramref name="json"/> onto <paramref name="target"/>; returns one message per value skipped.</summary>
    public static List<string> Populate(object target, JsonObject json, JsonSerializerOptions options)
    {
        var problems = new List<string>();
        Populate(target, json, options, path: string.Empty, problems);
        return problems;
    }

    private static void Populate(object target, JsonObject json, JsonSerializerOptions options, string path, List<string> problems)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || !TryFind(json, property.Name, out var key, out var node))
            {
                continue;
            }

            var propertyPath = path.Length == 0 ? key : $"{path}.{key}";
            var type = property.PropertyType;

            if (IsSection(type))
            {
                if (node is JsonObject section)
                {
                    var value = property.GetValue(target) ?? Activator.CreateInstance(type)!;
                    Populate(value, section, options, propertyPath, problems);
                    property.SetValue(target, value);
                }
                else
                {
                    problems.Add($"{propertyPath}: expected an object, kept the default");
                }

                continue;
            }

            if (IsEnumKeyedDictionary(type, out var keyType, out var valueType))
            {
                if (node is JsonObject entries)
                {
                    var dictionary = (System.Collections.IDictionary)(property.GetValue(target) ?? Activator.CreateInstance(type)!);
                    foreach (var (entryKey, entryNode) in entries)
                    {
                        if (!TryParseEnumName(keyType, entryKey, out var enumKey))
                        {
                            problems.Add($"{propertyPath}.{entryKey}: unknown name, ignored");
                            continue;
                        }

                        if (TryRead(entryNode, valueType, options, out var entryValue))
                        {
                            dictionary[enumKey!] = entryValue;
                        }
                        else
                        {
                            problems.Add($"{propertyPath}.{entryKey}: invalid value {Describe(entryNode)}, kept the default");
                        }
                    }

                    property.SetValue(target, dictionary);
                }
                else
                {
                    problems.Add($"{propertyPath}: expected an object, kept the default");
                }

                continue;
            }

            if (TryRead(node, type, options, out var leaf))
            {
                property.SetValue(target, leaf);
            }
            else
            {
                problems.Add($"{propertyPath}: invalid value {Describe(node)}, kept the default");
            }
        }
    }

    private static bool TryRead(JsonNode? node, Type type, JsonSerializerOptions options, out object? value)
    {
        value = null;
        var nonNullableValueType = type.IsValueType && Nullable.GetUnderlyingType(type) is null;
        if (node is null)
        {
            if (type == typeof(HotkeyGesture))
            {
                value = HotkeyGesture.None; // a null hotkey means "turned off"
                return true;
            }

            // null is a valid value for references and nullable values; anything else keeps its default.
            return !nonNullableValueType;
        }

        try
        {
            value = node.Deserialize(type, options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return false;
        }

        var enumType = Nullable.GetUnderlyingType(type) ?? type;
        if (value is not null && enumType.IsEnum && !Enum.IsDefined(enumType, value))
        {
            return false;
        }

        return value is not null || !nonNullableValueType;
    }

    /// <summary>Parses an enum member by name only; numbers ("42") are not names.</summary>
    private static bool TryParseEnumName(Type enumType, string name, out object? value)
    {
        value = null;
        if (name.Length == 0 || char.IsDigit(name[0]) || name[0] is '-' or '+')
        {
            return false;
        }

        return Enum.TryParse(enumType, name, ignoreCase: true, out value) && Enum.IsDefined(enumType, value!);
    }

    private static bool TryFind(JsonObject json, string propertyName, out string key, out JsonNode? node)
    {
        foreach (var (k, v) in json)
        {
            if (string.Equals(k, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                key = k;
                node = v;
                return true;
            }
        }

        key = propertyName;
        node = null;
        return false;
    }

    /// <summary>A nested settings class such as GeneralSettings: populated property by property.</summary>
    private static bool IsSection(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace == typeof(AppSettings).Namespace;

    private static bool IsEnumKeyedDictionary(Type type, out Type keyType, out Type valueType)
    {
        keyType = valueType = typeof(object);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>))
        {
            return false;
        }

        var args = type.GetGenericArguments();
        keyType = args[0];
        valueType = args[1];
        return keyType.IsEnum;
    }

    private static string Describe(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length > 40 ? text[..40] + "..." : text;
    }
}
