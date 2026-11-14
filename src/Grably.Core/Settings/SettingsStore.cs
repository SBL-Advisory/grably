using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Grably.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. A missing file gives defaults. Invalid individual values
/// (for example from a newer Grably) fall back to their defaults one by one and are logged. A file that is not
/// readable JSON at all is kept aside as settings.json.broken-*.json and replaced by defaults, so a bad edit never
/// stops the app starting. Saves go to a temporary file first and then replace the original.
/// </summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private AppSettings? _current;

    public SettingsStore(AppPaths paths, ILogger<SettingsStore>? logger = null)
        : this(paths.SettingsFile, logger)
    {
    }

    internal SettingsStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Raised after <see cref="Save"/> with the settings that were saved.</summary>
    public event EventHandler<AppSettings>? Saved;

    /// <summary>The settings, loaded from disk on first access.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            _current = settings;
        }

        _logger.LogInformation("Settings saved to {Path}", _path);
        Saved?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("No settings file at {Path}; using defaults", _path);
            return Defaults();
        }

        var text = TryReadText();
        if (text is null)
        {
            // Could not read the file (locked by a backup or antivirus scan, permissions). It may be fine, so leave it.
            return Defaults();
        }

        try
        {
            var json = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new JsonException("The settings file is not a JSON object.");

            var version = ReadSchemaVersion(json);
            if (version > AppSettings.CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "Settings file is schema {Version}, newer than this build ({Current}); unknown settings are ignored",
                    version, AppSettings.CurrentSchemaVersion);
            }
            else if (version < AppSettings.CurrentSchemaVersion)
            {
                SettingsMigrations.Apply(json, version);
                _logger.LogInformation("Migrated settings from schema {From} to {To}", version, AppSettings.CurrentSchemaVersion);
            }

            var settings = new AppSettings();
            foreach (var problem in TolerantSettingsReader.Populate(settings, json, JsonOptions))
            {
                _logger.LogWarning("Settings file: {Problem}", problem);
            }

            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only content that is not a JSON object at all ends up here (syntax errors, duplicate keys).
            var broken = $"{_path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            _logger.LogError(ex, "Could not read settings; using defaults and keeping the old file as {Broken}", broken);
            TryMove(_path, broken);
            return Defaults();
        }
    }

    private string? TryReadText()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    _logger.LogError(ex, "Could not read {Path}; using defaults for this session", _path);
                    return null;
                }

                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static int ReadSchemaVersion(JsonObject json)
    {
        foreach (var (key, value) in json)
        {
            if (string.Equals(key, nameof(AppSettings.SchemaVersion), StringComparison.OrdinalIgnoreCase)
                && value is JsonValue v && v.TryGetValue<int>(out var version))
            {
                return version;
            }
        }

        return 1;
    }

    private static AppSettings Defaults()
    {
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    private void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not move the unreadable settings file aside");
        }
    }
}
