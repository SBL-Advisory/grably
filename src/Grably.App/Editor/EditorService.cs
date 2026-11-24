using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Grably.App.Capture;
using Grably.App.Imaging;
using Grably.App.Library;
using Grably.App.Shell;
using Grably.Core;
using Grably.Core.Annotations;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Imaging;
using Grably.Core.Library;
using Grably.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grably.App.Editor;

/// <summary>Opens editor windows and does their saving, copying and exporting.</summary>
public sealed class EditorService(
    IServiceProvider services, SettingsStore settings, AppPaths paths, LibraryService library, UserNotifications notifications,
    ILogger<EditorService> logger)
{
    private readonly List<EditorWindow> _open = [];

    public IReadOnlyList<EditorWindow> OpenWindows => _open;

    /// <summary>Whether an open editor would write <paramref name="filePath"/> or <paramref name="projectPath"/> on Save.</summary>
    public bool IsEditing(string? filePath, string? projectPath) => _open.Any(w =>
        SamePath(w.Source.ImagePath, filePath) || SamePath(w.Source.ProjectPath, projectPath));

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A fresh capture: its file is already saved; Save overwrites it with the annotated version.</summary>
    public EditorWindow OpenCapture(CaptureResult capture, string imagePath) => Show(new EditorDocumentSource
    {
        Original = capture.Image,
        OriginalPng = EncodePng(capture.Image),
        Document = AnnotationDocument.ForImage(capture.Image.Width, capture.Image.Height),
        ImagePath = imagePath,
        ProjectPath = NewProjectPath(),
    });

    /// <summary>Opens a .grably project (editable annotations) or any image file.</summary>
    public EditorWindow? OpenFile(string path)
    {
        try
        {
            if (string.Equals(Path.GetExtension(path), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            {
                var (png, document, info) = ProjectFile.Load(path);
                var trusted = IsTrustedImagePath(info?.ImagePath, path);
                return Show(new EditorDocumentSource
                {
                    Original = Decode(png),
                    OriginalPng = png,
                    Document = document,
                    ImagePath = trusted ? info!.ImagePath : null,
                    OpenedFrom = trusted ? null : info?.ImagePath, // only a name and folder to suggest, never written to
                    ProjectPath = path,
                });
            }

            // Someone else's image: annotate it, but never save over it without asking (first Save is a Save as).
            var bytes = File.ReadAllBytes(path);
            var image = Decode(bytes);
            var isPng = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']);
            return Show(new EditorDocumentSource
            {
                Original = image,
                OriginalPng = isPng ? bytes : EncodePng(image),
                Document = AnnotationDocument.ForImage(image.Width, image.Height),
                ImagePath = null,
                OpenedFrom = path,
                ProjectPath = NewProjectPath(),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Could not open {Path}", path);
            notifications.Show($"Could not open {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Opens a library item: with its annotations when it has a project. Library files are Grably's own (captures
    /// and files saved from the editor), so Save writes back to the item's file.
    /// </summary>
    public EditorWindow? OpenLibraryItem(LibraryItem item)
    {
        try
        {
            var file = item.FilePath is { } f && File.Exists(f) ? f : null;
            if (item.ProjectPath is { } project && File.Exists(project))
            {
                var (png, document, info) = ProjectFile.Load(project);
                return Show(new EditorDocumentSource
                {
                    Original = Decode(png),
                    OriginalPng = png,
                    Document = document,
                    ImagePath = file,
                    OpenedFrom = file is null ? info?.ImagePath : null,
                    ProjectPath = project,
                });
            }

            if (file is null)
            {
                notifications.Show($"{item.Title} is no longer on disk.");
                return null;
            }

            var bytes = File.ReadAllBytes(file);
            var image = Decode(bytes);
            var isPng = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']);
            return Show(new EditorDocumentSource
            {
                Original = image,
                OriginalPng = isPng ? bytes : EncodePng(image),
                Document = AnnotationDocument.ForImage(image.Width, image.Height),
                ImagePath = file,
                ProjectPath = NewProjectPath(),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Could not open library item {Id}", item.Id);
            notifications.Show($"Could not open {item.Title}: {ex.Message}");
            return null;
        }
    }

    /// <summary>An image from the clipboard becomes a new, unsaved document.</summary>
    public EditorWindow? OpenClipboardImage() => Clipboard.GetImage() is { } bitmap ? OpenImage(bitmap) : null;

    /// <summary>Any picture (a frame from the trim editor, the clipboard) as a new, unsaved document.</summary>
    public EditorWindow OpenImage(BitmapSource bitmap)
    {
        var image = bitmap.ToPixelBuffer();
        return Show(new EditorDocumentSource
        {
            Original = image,
            OriginalPng = EncodePng(image),
            Document = AnnotationDocument.ForImage(image.Width, image.Height),
            ImagePath = null,
            ProjectPath = NewProjectPath(),
        });
    }

    /// <summary>Writes the flattened image over the document's file and keeps the project in sync.</summary>
    public bool Save(EditorDocumentSource source, AnnotationDocument document)
    {
        if (source.ImagePath is not { } imagePath)
        {
            return false;
        }

        try
        {
            var flattened = WriteImage(source, document, imagePath);
            ProjectFile.Save(source.ProjectPath, source.OriginalPng, document, new ProjectInfo(imagePath, DateTime.UtcNow));
            library.RecordSave(imagePath, source.ProjectPath, flattened);
            logger.LogInformation("Saved {Count} annotation(s) to {Image} and project {Project}", document.Items.Count, imagePath, source.ProjectPath);
            notifications.Show($"Saved {Path.GetFileName(imagePath)}");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Saving {Image} failed", imagePath);
            notifications.Show($"Could not save {Path.GetFileName(imagePath)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes a copy elsewhere. An unsaved document (no file yet) adopts the new file as its own.</summary>
    public bool SaveAs(EditorDocumentSource source, AnnotationDocument document, string path)
    {
        try
        {
            var flattened = WriteImage(source, document, path);
            if (source.ImagePath is null)
            {
                source.ImagePath = path;
                ProjectFile.Save(source.ProjectPath, source.OriginalPng, document, new ProjectInfo(path, DateTime.UtcNow));
                library.RecordSave(path, source.ProjectPath, flattened);
            }

            logger.LogInformation("Saved a copy to {Path}", path);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Saving {Path} failed", path);
            notifications.Show($"Could not save {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Copies the text of the original image inside the crop (annotations are not read).</summary>
    public Task CopyTextAsync(EditorDocumentSource source, AnnotationDocument document)
    {
        var area = document.ViewArea;
        var image = area == new PixelRect(0, 0, source.Original.Width, source.Original.Height) ? source.Original : source.Original.Crop(area);
        return services.GetRequiredService<Text.TextCaptureService>().CopyTextAsync(image, "Editor text copy");
    }

    public void CopyImage(EditorDocumentSource source, AnnotationDocument document)
    {
        try
        {
            ImageOutput.CopyToClipboard(AnnotationRenderer.Flatten(document, source.Original));
            notifications.Show("Image copied to the clipboard");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Copy to clipboard failed");
            notifications.Show("The clipboard is busy; try copying again.");
        }
    }

    /// <summary>
    /// Asks every editor with unsaved changes whether to save (tray Exit). Returns false if the user cancelled, in
    /// which case nothing should close.
    /// </summary>
    public bool ConfirmCloseAll() => CloseCoordinator.ConfirmAll(_open);

    /// <summary>
    /// Windows is signing out or shutting down and there is no time to ask: keep every unsaved document's
    /// annotations in its project (the image file is left untouched), then let the editors close without a prompt.
    /// </summary>
    public void SaveForSessionEnd() => CloseCoordinator.SessionEnding(_open);

    /// <summary>
    /// Writes only the project (never the image), for sign-out. The image path recorded is one Grably may write
    /// (a capture or a Save as result), never a file the user merely opened.
    /// </summary>
    public void KeepForLater(EditorDocumentSource source, AnnotationDocument document)
    {
        try
        {
            ProjectFile.Save(source.ProjectPath, source.OriginalPng, document, new ProjectInfo(source.ImagePath, DateTime.UtcNow));
            library.RecordKeptProject(source.ImagePath, source.ProjectPath, source.DisplayName, AnnotationRenderer.Flatten(document, source.Original));
            logger.LogWarning("Session ending: kept unsaved annotations of {Name} in {Project}", source.DisplayName, source.ProjectPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Session ending: could not keep the annotations of {Name}", source.DisplayName);
        }
    }

    /// <summary>
    /// Whether Save may write over a project's recorded image path without asking: it must be an existing PNG or
    /// JPEG, and either inside a screenshots folder or belong to one of Grably's own projects. A project from
    /// elsewhere could otherwise aim Save at any image on the machine.
    /// </summary>
    public bool IsTrustedImagePath(string? imagePath, string projectPath) =>
        EditorDocumentSource.IsTrustedImagePath(imagePath, projectPath, paths.ProjectsFolder, ScreenshotFolders());

    private IEnumerable<string> ScreenshotFolders()
    {
        yield return paths.DefaultScreenshotsFolder;
        if (settings.Current.General.ScreenshotsFolder is { Length: > 0 } custom)
        {
            yield return custom;
        }
    }

    /// <summary>A flattened copy in the temp folder, for dragging onto other apps.</summary>
    public string WriteTemporaryCopy(EditorDocumentSource source, AnnotationDocument document)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Grably", "drag");
        Directory.CreateDirectory(folder);
        var name = (source.ImagePath ?? source.OpenedFrom) is { } p ? Path.GetFileNameWithoutExtension(p) : "Grably";
        var path = Path.Combine(folder, name + ".png");
        ImageOutput.Save(AnnotationRenderer.Flatten(document, source.Original), path, ImageFormat.Png, 100);
        return path;
    }

    private BitmapSource WriteImage(EditorDocumentSource source, AnnotationDocument document, string path)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        var flattened = AnnotationRenderer.Flatten(document, source.Original);
        ImageOutput.Save(flattened, path, format, settings.Current.Screenshot.JpegQuality);
        return flattened;
    }

    private EditorWindow Show(EditorDocumentSource source)
    {
        var window = ActivatorUtilities.CreateInstance<EditorWindow>(services, source);
        window.SizeToImage();
        _open.Add(window);
        window.Closed += (_, _) => _open.Remove(window);
        window.Show();
        window.Activate();
        logger.LogInformation("Editor opened for {Name} ({Width}x{Height})", source.DisplayName, source.Original.Width, source.Original.Height);
        return window;
    }

    private string NewProjectPath() => Path.Combine(paths.ProjectsFolder, $"{Guid.NewGuid():N}{ProjectFile.Extension}");

    private static byte[] EncodePng(PixelBuffer image)
    {
        using var stream = new MemoryStream();
        PngWriter.Write(image, stream);
        return stream.ToArray();
    }

    private static PixelBuffer Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return frame.ToPixelBuffer();
    }
}
