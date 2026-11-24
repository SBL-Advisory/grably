using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grably.Core.Recording;

public enum SessionState
{
    /// <summary>Frames are being written; a session still in this state after a restart crashed.</summary>
    Recording,

    /// <summary>The intermediate is complete and was turned into the final video.</summary>
    Finished,

    /// <summary>The recording failed; the intermediate may still hold what was recorded.</summary>
    Failed,
}

/// <summary>
/// A recording in progress, kept in <c>sessions/{id}</c> beside its crash-safe Matroska intermediate, so that a
/// recording interrupted by a crash can be found and recovered on the next start.
/// </summary>
public sealed record RecordingSession
{
    public const string FileName = "session.json";
    public const string VideoFileName = "video.mkv";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public required string Id { get; init; }

    public required DateTime StartedUtc { get; init; }

    /// <summary>What was recorded, for people: "Monitor 1", "Window: Notepad", "Region 800x600 at 100,200".</summary>
    public required string Target { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int FrameRate { get; init; }

    public required string Encoder { get; init; }

    public SessionState State { get; init; } = SessionState.Recording;

    /// <summary>Audio track files in the session folder (raw 48 kHz stereo float), mixed into the MP4 at the end.</summary>
    public IReadOnlyList<string> AudioTracks { get; init; } = [];

    /// <summary>The finished video, once there is one.</summary>
    public string? OutputPath { get; init; }

    public static string NewId(DateTime now) => $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// Sessions in <paramref name="sessionsFolder"/> that recorded video but were never saved as an MP4 (Grably or
    /// Windows stopped mid-recording). Offered back at startup.
    /// </summary>
    public static IReadOnlyList<(RecordingSession Session, string Folder)> FindUnsaved(string sessionsFolder)
    {
        var found = new List<(RecordingSession, string)>();
        if (!Directory.Exists(sessionsFolder))
        {
            return found;
        }

        foreach (var folder in Directory.GetDirectories(sessionsFolder))
        {
            var video = Path.Combine(folder, VideoFileName);
            if (Load(folder) is { OutputPath: null } session && File.Exists(video) && new FileInfo(video).Length > 0)
            {
                found.Add((session, folder));
            }
        }

        return found;
    }

    public static RecordingSession? Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<RecordingSession>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
