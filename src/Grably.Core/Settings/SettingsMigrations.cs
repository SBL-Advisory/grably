using System.Text.Json.Nodes;

namespace Grably.Core.Settings;

/// <summary>
/// Upgrades the raw JSON of an older settings file, one schema version at a time, before it is deserialised.
/// To change the shape of an existing setting: bump <see cref="AppSettings.CurrentSchemaVersion"/> and add a step
/// keyed by the version it upgrades from.
/// </summary>
internal static class SettingsMigrations
{
    private static readonly Dictionary<int, Action<JsonObject>> Steps = new()
    {
        // [1] = json => { ... upgrade a version 1 document to version 2 ... },
    };

    /// <summary>Applies every step from <paramref name="fromVersion"/> up to the current version.</summary>
    public static void Apply(JsonObject json, int fromVersion)
    {
        for (var version = fromVersion; version < AppSettings.CurrentSchemaVersion; version++)
        {
            if (Steps.TryGetValue(version, out var step))
            {
                step(json);
            }
        }

        var existing = json.Select(p => p.Key)
            .Where(k => string.Equals(k, nameof(AppSettings.SchemaVersion), StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in existing)
        {
            json.Remove(key);
        }

        json["schemaVersion"] = AppSettings.CurrentSchemaVersion;
    }
}
