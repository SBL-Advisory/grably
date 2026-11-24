using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grably.Core.Recording;

/// <summary>A stretch of time, start included and end excluded.</summary>
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public bool Contains(TimeSpan time) => time >= Start && time < End;
}

/// <summary>
/// Which parts of a recording are kept, in the recording's own time. Trimming the ends and cutting out a middle
/// stretch both just shrink the kept parts; the recording itself is never changed. Immutable: every edit returns a
/// new list, which makes undo and redo a list of versions.
/// </summary>
public sealed record EditList
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Kept parts shorter than this are dropped (a sliver left by an imprecise cut). Well under one frame even at
    /// 120 fps, so a single kept frame always stays.
    /// </summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromMilliseconds(5);

    [JsonConstructor]
    public EditList(TimeSpan sourceDuration, IReadOnlyList<TimeRange> kept)
    {
        SourceDuration = sourceDuration;
        Kept = Normalize(kept, sourceDuration);
    }

    public TimeSpan SourceDuration { get; }

    /// <summary>The kept parts in order, never overlapping or empty.</summary>
    public IReadOnlyList<TimeRange> Kept { get; }

    /// <summary>The length of the edited video.</summary>
    [JsonIgnore]
    public TimeSpan Duration => Kept.Aggregate(TimeSpan.Zero, (sum, range) => sum + range.Duration);

    /// <summary>Nothing has been removed.</summary>
    [JsonIgnore]
    public bool IsWhole => Kept.Count == 1 && Kept[0].Start == TimeSpan.Zero && Kept[0].End == SourceDuration;

    public static EditList Whole(TimeSpan duration) => new(duration, [new TimeRange(TimeSpan.Zero, duration)]);

    /// <summary>Keeps only what lies between <paramref name="start"/> and <paramref name="end"/> (trimming both ends).</summary>
    public EditList Trim(TimeSpan start, TimeSpan end) =>
        new(SourceDuration, Kept.Select(r => new TimeRange(Max(r.Start, start), Min(r.End, end))).ToList());

    /// <summary>
    /// Moves the ends of the edited video to <paramref name="start"/> and <paramref name="end"/> (the trim handles):
    /// moving an end inwards trims, moving it outwards brings that stretch back. Cuts in between stay.
    /// </summary>
    public EditList WithEnds(TimeSpan start, TimeSpan end)
    {
        if (Kept.Count == 0)
        {
            return new EditList(SourceDuration, [new TimeRange(start, end)]);
        }

        var kept = Kept.Select(r => new TimeRange(Max(r.Start, start), Min(r.End, end))).ToList();
        kept.Add(new TimeRange(start, Min(Kept[0].Start, end)));
        kept.Add(new TimeRange(Max(Kept[^1].End, start), end));
        return new EditList(SourceDuration, kept);
    }

    /// <summary>Where the edited video starts in the recording.</summary>
    [JsonIgnore]
    public TimeSpan Start => Kept.Count > 0 ? Kept[0].Start : TimeSpan.Zero;

    /// <summary>Where the edited video ends in the recording.</summary>
    [JsonIgnore]
    public TimeSpan End => Kept.Count > 0 ? Kept[^1].End : TimeSpan.Zero;

    /// <summary>Removes <paramref name="range"/>, splitting a kept part when the cut falls inside it.</summary>
    public EditList Cut(TimeRange range)
    {
        var kept = new List<TimeRange>();
        foreach (var part in Kept)
        {
            kept.Add(new TimeRange(part.Start, Min(part.End, range.Start)));
            kept.Add(new TimeRange(Max(part.Start, range.End), part.End));
        }

        return new EditList(SourceDuration, kept);
    }

    public bool IsKept(TimeSpan time) => Kept.Any(r => r.Contains(time));

    /// <summary>For playback: <paramref name="time"/> if it is kept, else the start of the next kept part, or null after the last.</summary>
    public TimeSpan? Playable(TimeSpan time) =>
        IsKept(time) ? time : Kept.FirstOrDefault(r => r.Start > time) is { Duration.Ticks: > 0 } next ? next.Start : null;

    /// <summary>Where a moment of the recording ends up in the edited video (removed moments map to where the cut is).</summary>
    public TimeSpan ToEdited(TimeSpan time)
    {
        var edited = TimeSpan.Zero;
        foreach (var range in Kept)
        {
            if (time < range.Start)
            {
                break;
            }

            edited += Min(time, range.End) - range.Start;
        }

        return edited;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null for anything that is not a valid edit list.</summary>
    public static EditList? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EditList>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool Equals(EditList? other) => other is not null && SourceDuration == other.SourceDuration && Kept.SequenceEqual(other.Kept);

    public override int GetHashCode() => HashCode.Combine(SourceDuration, Kept.Count);

    private static List<TimeRange> Normalize(IEnumerable<TimeRange> ranges, TimeSpan duration)
    {
        var merged = new List<TimeRange>();
        foreach (var range in ranges
                     .Select(r => new TimeRange(Max(r.Start, TimeSpan.Zero), Min(r.End, duration)))
                     .Where(r => r.Duration >= Shortest)
                     .OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Max(merged[^1].End, range.End) };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>Undo and redo over edit lists.</summary>
public sealed class EditHistory(EditList initial)
{
    private readonly List<EditList> _versions = [initial];
    private int _current;

    public EditList Current => _versions[_current];

    public bool CanUndo => _current > 0;

    public bool CanRedo => _current < _versions.Count - 1;

    public void Apply(EditList next)
    {
        if (next.Equals(Current))
        {
            return;
        }

        _versions.RemoveRange(_current + 1, _versions.Count - _current - 1);
        _versions.Add(next);
        _current++;
    }

    public void Undo() => _current = Math.Max(0, _current - 1);

    public void Redo() => _current = Math.Min(_versions.Count - 1, _current + 1);
}

/// <summary>What to export.</summary>
public enum ExportFormat
{
    Mp4,
    Gif,
}

/// <param name="MaxHeight">Scale down taller videos to this height; null keeps the size.</param>
/// <param name="GifFrameRate">GIF only: frames per second (fewer makes a smaller file).</param>
/// <param name="GifWidth">GIF only: width in pixels.</param>
public sealed record ExportSettings(ExportFormat Format, int? MaxHeight = null, int GifFrameRate = 12, int GifWidth = 800);
