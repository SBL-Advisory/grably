namespace Grably.Core.Library;

public enum LibraryItemKind
{
    Screenshot,
    Recording,
    Gif,
}

/// <summary>
/// One entry in the capture library. At least one of <see cref="FilePath"/> and <see cref="ProjectPath"/> is set:
/// a capture has its image file (and a project once annotated); annotations kept at sign-out for an image Grably
/// may not overwrite have only the project.
/// </summary>
public sealed record LibraryItem
{
    public long Id { get; init; }

    public LibraryItemKind Kind { get; init; }

    /// <summary>The image or video file the user sees.</summary>
    public string? FilePath { get; init; }

    /// <summary>The .grably project that keeps the annotations editable.</summary>
    public string? ProjectPath { get; init; }

    public string? ThumbnailPath { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Process name of the app the capture came from.</summary>
    public string? SourceApp { get; init; }

    /// <summary>Title of the window the capture came from.</summary>
    public string? SourceTitle { get; init; }

    public DateTime CreatedUtc { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public long? DurationMs { get; init; }

    public long Bytes { get; init; }

    /// <summary>Text recognised in the image (M5), for search.</summary>
    public string? OcrText { get; init; }
}

/// <summary>What the library shows: newest first, optionally filtered by kind and search text.</summary>
public sealed record LibraryQuery(string? Text = null, LibraryItemKind? Kind = null, int Limit = 1000, int Offset = 0);
