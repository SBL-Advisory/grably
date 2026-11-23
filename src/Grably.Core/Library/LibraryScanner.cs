using System.Text.Json;
using Grably.Core.Annotations;

namespace Grably.Core.Library;

/// <summary>
/// Rebuilds library items from what is on disk, for a library that starts empty: the first run of a Grably with a
/// library, or after a damaged library was set aside. Projects come first because the library is the only other
/// link from a capture to its annotations (each project records the image it flattens to).
/// </summary>
public static class LibraryScanner
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>Items for every project in <paramref name="projectsFolder"/> and every image directly in the folders.</summary>
    public static List<LibraryItem> Scan(string projectsFolder, IEnumerable<string> imageFolders)
    {
        var items = new List<LibraryItem>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in Files(projectsFolder).Where(f => f.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)))
        {
            ProjectInfo? info;
            try
            {
                info = ProjectFile.LoadInfo(project);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                continue; // not a readable project; leave it alone
            }

            var image = info?.ImagePath is { } path && File.Exists(path) ? Path.GetFullPath(path) : null;
            if (image is not null && !files.Add(image))
            {
                image = null; // another project already claims that file
            }

            var name = Path.GetFileNameWithoutExtension(info?.ImagePath ?? project);
            items.Add(new LibraryItem
            {
                Kind = LibraryItemKind.Screenshot,
                FilePath = image,
                ProjectPath = project,
                Title = image is null ? $"Annotations of {name}" : name,
                CreatedUtc = info?.CreatedUtc.ToUniversalTime() ?? File.GetCreationTimeUtc(project),
                Bytes = image is null ? 0 : new FileInfo(image).Length,
            });
        }

        foreach (var folder in imageFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in Files(folder))
            {
                if (ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) && files.Add(file))
                {
                    items.Add(new LibraryItem
                    {
                        Kind = LibraryItemKind.Screenshot,
                        FilePath = file,
                        Title = Path.GetFileNameWithoutExtension(file),
                        CreatedUtc = File.GetCreationTimeUtc(file),
                        Bytes = new FileInfo(file).Length,
                    });
                }
            }
        }

        return items;
    }

    private static IEnumerable<string> Files(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(Path.GetFullPath(folder)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
