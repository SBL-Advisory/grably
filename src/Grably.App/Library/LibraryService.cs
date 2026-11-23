using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.Core;
using Grably.Core.Capture;
using Grably.Core.Library;
using Grably.Core.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Grably.App.Library;

/// <summary>
/// Keeps the library in step with what Grably saves: captures, editor saves, recovered annotations. Owns the
/// thumbnails. Library problems are logged and never stop a capture or a save from succeeding.
/// </summary>
public sealed class LibraryService(AppPaths paths, ILogger<LibraryService> logger, SettingsStore? settings = null)
{
    public const int ThumbnailWidth = 360;
    public const int ThumbnailHeight = 240;

    private readonly object _openLock = new();
    private LibraryRepository? _repository;

    /// <summary>Raised on the calling thread after the library changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The library database, opened on first use. A failure is not remembered (a locked file may be free on the next
    /// call). A damaged database is set aside, and a new library is filled from the projects and screenshots on disk.
    /// </summary>
    public LibraryRepository Repository
    {
        get
        {
            lock (_openLock)
            {
                return _repository ??= Open();
            }
        }
    }

    /// <summary>
    /// After a library error: if it says the database is damaged, forget the open library so the next use checks it
    /// again (and sets it aside). Other errors (a locked file) are left to retry as they are.
    /// </summary>
    public void ReportError(Exception error)
    {
        if (IsDamage(error))
        {
            lock (_openLock)
            {
                _repository = null;
            }
        }
    }

    private static bool IsDamage(Exception error) => error is SqliteException { SqliteErrorCode: 11 or 26 }; // CORRUPT, NOTADB

    private LibraryRepository Open()
    {
        var isNew = !File.Exists(paths.LibraryDatabase);
        LibraryRepository? repository = null;
        try
        {
            repository = new LibraryRepository(paths.LibraryDatabase);
            if (!isNew && !repository.IsIntact())
            {
                logger.LogError("The library database failed its integrity check");
                repository = null;
            }
        }
        catch (SqliteException ex) when (IsDamage(ex))
        {
            logger.LogError(ex, "The library database is damaged");
        }

        if (repository is null)
        {
            SetAside();
            repository = new LibraryRepository(paths.LibraryDatabase);
            isNew = true;
        }

        if (isNew)
        {
            Rebuild(repository);
        }

        return repository;
    }

    private void SetAside()
    {
        var aside = $"{paths.LibraryDatabase}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
        logger.LogWarning("Moving the damaged library to {Path} and starting a new one", aside);
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(paths.LibraryDatabase + suffix))
            {
                File.Move(paths.LibraryDatabase + suffix, aside + suffix);
            }
        }
    }

    /// <summary>A new library starts with what is already on disk, so earlier captures and their annotations show up.</summary>
    private void Rebuild(LibraryRepository repository)
    {
        var folders = new List<string> { paths.DefaultScreenshotsFolder };
        if (settings?.Current.General.ScreenshotsFolder is { } custom)
        {
            folders.Add(custom);
        }

        var added = repository.AddRange(LibraryScanner.Scan(paths.ProjectsFolder, folders));
        logger.LogInformation("New library filled with {Count} item(s) found on disk", added);
    }

    /// <summary>Records a new screenshot with its thumbnail. Returns null if the library could not be updated.</summary>
    public LibraryItem? AddCapture(CaptureResult capture, string filePath, BitmapSource image) => Guarded("add a capture", () =>
    {
        var item = new LibraryItem
        {
            Kind = LibraryItemKind.Screenshot,
            FilePath = filePath,
            ThumbnailPath = WriteThumbnail(image),
            Title = Path.GetFileNameWithoutExtension(filePath),
            SourceApp = capture.Source.ProcessName,
            SourceTitle = capture.Source.WindowTitle,
            CreatedUtc = capture.CapturedAt.ToUniversalTime(),
            Width = image.PixelWidth,
            Height = image.PixelHeight,
            Bytes = SizeOf(filePath),
        };
        return item with { Id = Repository.Add(item) };
    });

    /// <summary>Records a finished screen recording, with a thumbnail made from one of its frames.</summary>
    public LibraryItem? AddRecording(
        string filePath, TimeSpan duration, int width, int height, BitmapSource? frame, string? sourceApp, string? sourceTitle, DateTime started) =>
        Guarded("add a recording", () =>
        {
            var item = new LibraryItem
            {
                Kind = LibraryItemKind.Recording,
                FilePath = filePath,
                ThumbnailPath = frame is null ? null : WriteThumbnail(frame),
                Title = Path.GetFileNameWithoutExtension(filePath),
                SourceApp = sourceApp,
                SourceTitle = sourceTitle,
                CreatedUtc = started.ToUniversalTime(),
                Width = width,
                Height = height,
                DurationMs = (long)duration.TotalMilliseconds,
                Bytes = SizeOf(filePath),
            };
            return item with { Id = Repository.Add(item) };
        });

    /// <summary>
    /// The editor saved <paramref name="filePath"/> (and its project): refresh the item's thumbnail, size and project
    /// link, or add the file as a new item when the library did not know it (first Save as of a new document).
    /// </summary>
    public LibraryItem? RecordSave(string filePath, string projectPath, BitmapSource flattened) => Guarded("record a save", () =>
    {
        var existing = Repository.FindByFile(filePath) ?? Repository.FindByProject(projectPath);
        var thumbnail = WriteThumbnail(flattened);
        if (existing is not null)
        {
            DeleteThumbnail(existing.ThumbnailPath);
            var updated = existing with
            {
                // A project-only item ("Unsaved annotations of ...") is named after the file it now has.
                Title = existing.FilePath is null ? Path.GetFileNameWithoutExtension(filePath) : existing.Title,
                FilePath = filePath,
                ProjectPath = projectPath,
                ThumbnailPath = thumbnail,
                Width = flattened.PixelWidth,
                Height = flattened.PixelHeight,
                Bytes = SizeOf(filePath),
                OcrText = null, // read again: the saved image has new text annotations, or a crop
            };
            Repository.Update(updated);
            return updated;
        }

        var item = new LibraryItem
        {
            Kind = LibraryItemKind.Screenshot,
            FilePath = filePath,
            ProjectPath = projectPath,
            ThumbnailPath = thumbnail,
            Title = Path.GetFileNameWithoutExtension(filePath),
            CreatedUtc = DateTime.UtcNow,
            Width = flattened.PixelWidth,
            Height = flattened.PixelHeight,
            Bytes = SizeOf(filePath),
        };
        return item with { Id = Repository.Add(item) };
    });

    /// <summary>
    /// Annotations were kept in a project at sign-out. Link the project to its item, or, for an image Grably may
    /// not overwrite, add a project-only item so the work can be found and reopened.
    /// </summary>
    public void RecordKeptProject(string? filePath, string projectPath, string displayName, BitmapSource flattened) => Guarded("record kept annotations", () =>
    {
        var existing = (filePath is null ? null : Repository.FindByFile(filePath)) ?? Repository.FindByProject(projectPath);
        if (existing is not null)
        {
            Repository.Update(existing with { ProjectPath = projectPath });
            return existing;
        }

        var item = new LibraryItem
        {
            Kind = LibraryItemKind.Screenshot,
            ProjectPath = projectPath,
            ThumbnailPath = WriteThumbnail(flattened),
            Title = $"Unsaved annotations of {displayName}",
            CreatedUtc = DateTime.UtcNow,
            Width = flattened.PixelWidth,
            Height = flattened.PixelHeight,
        };
        return item with { Id = Repository.Add(item) };
    });

    /// <summary>Points the item at its renamed file. Returns null if the library could not be updated.</summary>
    public LibraryItem? Rename(LibraryItem item, string newFilePath) => Guarded("rename an item", () =>
    {
        var renamed = item with { FilePath = newFilePath, Title = Path.GetFileNameWithoutExtension(newFilePath) };
        Repository.Update(renamed);
        return renamed;
    });

    /// <summary>Forgets the items (their files are handled by the caller) and deletes their thumbnails.</summary>
    public void Remove(IEnumerable<LibraryItem> items) => Guarded("remove items", () =>
    {
        foreach (var item in items)
        {
            Repository.Delete(item.Id);
            DeleteThumbnail(item.ThumbnailPath);
        }

        return (LibraryItem?)null;
    });

    /// <summary>Whether the item's file (or, for a project-only item, its project) is gone.</summary>
    public static bool IsMissing(LibraryItem item) =>
        item.FilePath is { } file ? !File.Exists(file) && (item.ProjectPath is null || !File.Exists(item.ProjectPath))
        : item.ProjectPath is null || !File.Exists(item.ProjectPath);

    private string WriteThumbnail(BitmapSource image)
    {
        var scale = Math.Min(1, Math.Min((double)ThumbnailWidth / image.PixelWidth, (double)ThumbnailHeight / image.PixelHeight));
        BitmapSource thumbnail = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(thumbnail));
        Directory.CreateDirectory(paths.ThumbnailsFolder);
        var path = Path.Combine(paths.ThumbnailsFolder, $"{Guid.NewGuid():N}.jpg");
        using (var file = File.Create(path))
        {
            encoder.Save(file);
        }

        return path;
    }

    private void DeleteThumbnail(string? path)
    {
        // Only ever delete inside our own thumbnails folder, whatever the database says.
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ThumbnailsFolder)) + Path.DirectorySeparatorChar;
        if (path is null || !Path.GetFullPath(path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not delete thumbnail {Path}", path);
        }
    }

    private static long SizeOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private LibraryItem? Guarded(string what, Func<LibraryItem?> action)
    {
        LibraryItem? result;
        try
        {
            result = action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Could not {What} in the library", what);
            ReportError(ex);
            return null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }
}
