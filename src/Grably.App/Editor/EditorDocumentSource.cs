using System.IO;
using Grably.Core.Annotations;
using Grably.Core.Imaging;

namespace Grably.App.Editor;

/// <summary>What an editor window is editing: the original pixels and where the result is saved.</summary>
public sealed class EditorDocumentSource
{
    /// <summary>The untouched image the annotations are drawn over.</summary>
    public required PixelBuffer Original { get; init; }

    /// <summary>The original encoded as PNG, stored in the project so it can be edited again later.</summary>
    public required byte[] OriginalPng { get; init; }

    /// <summary>Annotations so far (empty for a fresh capture).</summary>
    public required AnnotationDocument Document { get; init; }

    /// <summary>
    /// The flattened image file that Save overwrites without asking. Only set for files Grably made (captures, and
    /// files the user picked in Save as); null means the first Save asks where to save.
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>A file the user opened: suggests the name and folder for the first Save as, never overwritten.</summary>
    public string? OpenedFrom { get; init; }

    /// <summary>The .grably project kept in sync on save; created on the first save.</summary>
    public required string ProjectPath { get; init; }

    public string DisplayName => Path.GetFileName(ImagePath ?? OpenedFrom) is { Length: > 0 } name ? name : "Untitled";

    /// <summary>
    /// Whether a project's recorded image path may be saved over without asking: an existing PNG or JPEG that is
    /// either inside one of the <paramref name="screenshotFolders"/>, or recorded by one of Grably's own projects
    /// (the project file lives in <paramref name="projectsFolder"/>). A project received from elsewhere could
    /// otherwise aim Save at any image on the machine.
    /// </summary>
    public static bool IsTrustedImagePath(string? imagePath, string projectPath, string projectsFolder, IEnumerable<string> screenshotFolders) =>
        imagePath is { Length: > 0 }
        && Path.IsPathFullyQualified(imagePath)
        && Path.GetExtension(imagePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"
        && File.Exists(imagePath)
        && (IsInside(projectPath, projectsFolder) || screenshotFolders.Any(folder => IsInside(imagePath, folder)));

    private static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
