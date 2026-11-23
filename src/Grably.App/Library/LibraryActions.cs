using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Grably.App.Capture;
using Grably.App.Editor;
using Grably.App.Shell;
using Grably.Core.Annotations;
using Grably.Core.Library;
using Grably.Platform.Shell;
using Microsoft.Extensions.Logging;

namespace Grably.App.Library;

/// <summary>What the library can do with its items. Every action reports problems to the user instead of throwing.</summary>
public sealed class LibraryActions(
    LibraryService library, EditorService editors, UserNotifications notifications, ILogger<LibraryActions> logger)
{
    /// <summary>Screenshots open in the editor; recordings in the default video player until the trim editor exists (M9).</summary>
    public void Open(LibraryItem item)
    {
        if (item.Kind == LibraryItemKind.Screenshot)
        {
            editors.OpenLibraryItem(item);
        }
        else if (Exists(item))
        {
            Try("open the video", () => Process.Start(new ProcessStartInfo(item.FilePath!) { UseShellExecute = true }));
        }
    }

    public void CopyImage(LibraryItem item)
    {
        if (!Exists(item))
        {
            return;
        }

        Try("copy the image", () =>
        {
            // Through a stream: a file name with '#' or '%' would be misread as a URI.
            using var stream = File.OpenRead(item.FilePath!);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            ImageOutput.CopyToClipboard(image);
            notifications.Show("Image copied to the clipboard");
        });
    }

    public void CopyPath(LibraryItem item)
    {
        if (item.FilePath is { } path)
        {
            Try("copy the path", () => Clipboard.SetText(path));
        }
    }

    public void ShowInFolder(LibraryItem item)
    {
        var path = item.FilePath is { } f && File.Exists(f) ? f : item.ProjectPath;
        if (path is not null && File.Exists(path))
        {
            Try("open the folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }));
        }
        else
        {
            notifications.Show($"{item.Title} is no longer on disk.");
        }
    }

    /// <summary>Renames the file in its folder, keeping the extension. Returns false (and says why) if it cannot.</summary>
    public bool Rename(LibraryItem item, string newName)
    {
        if (!Exists(item) || IsBeingEdited(item, "renaming"))
        {
            return false;
        }

        newName = newName.Trim();
        if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            notifications.Show("That name cannot be used for a file.");
            return false;
        }

        var target = Path.Combine(Path.GetDirectoryName(item.FilePath!)!, newName + Path.GetExtension(item.FilePath));
        if (string.Equals(target, item.FilePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (File.Exists(target) && !string.Equals(target, item.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            notifications.Show($"There is already a file called {Path.GetFileName(target)}.");
            return false;
        }

        var source = item.FilePath!;
        return Try("rename the file", () =>
        {
            File.Move(source, target);
            if (library.Rename(item, target) is null)
            {
                File.Move(target, source); // keep the file where the library (and its project) expect it
                throw new IOException("The library could not be updated, so the file keeps its old name.");
            }

            // The project remembers which image it flattens to; Grably.exe <project> must save to the new name.
            if (item.ProjectPath is { } project && File.Exists(project))
            {
                try
                {
                    ProjectFile.SaveInfo(project, new ProjectInfo(target, ProjectFile.Load(project).Info?.CreatedUtc ?? DateTime.UtcNow));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The rename itself worked; opening the project from the library still finds the new name.
                    logger.LogWarning(ex, "Could not update the image path in {Project}", project);
                }
            }
        });
    }

    /// <summary>
    /// Moves the items' files and projects to the Recycle Bin and forgets the items whose files are gone. Windows asks
    /// before destroying a file it cannot recycle; files the user keeps stay in the library.
    /// </summary>
    public void Delete(IReadOnlyCollection<LibraryItem> items, nint owner = 0)
    {
        var deletable = items.Where(i => !IsBeingEdited(i, "deleting")).ToList();
        if (deletable.Count == 0)
        {
            return;
        }

        // Images first; an item's annotations go only once its image is really gone (the user may keep a file that
        // could not be recycled, and it must keep its project).
        var images = deletable.Select(i => i.FilePath).OfType<string>().ToList();
        if (!RecycleBin.Send(images, owner))
        {
            logger.LogWarning("The Recycle Bin did not take all of {Count} image(s)", images.Count);
        }

        var projects = deletable.Where(i => !(i.FilePath is { } f && File.Exists(f))).Select(i => i.ProjectPath).OfType<string>().ToList();
        if (!RecycleBin.Send(projects, owner))
        {
            logger.LogWarning("The Recycle Bin did not take all of {Count} project(s)", projects.Count);
        }

        var gone = deletable.Where(i => !(i.FilePath is { } f && File.Exists(f)) && !(i.ProjectPath is { } p && File.Exists(p))).ToList();
        library.Remove(gone);
        if (gone.Count < deletable.Count)
        {
            notifications.Show(deletable.Count == 1
                ? $"{deletable[0].Title} was not deleted."
                : $"{deletable.Count - gone.Count} of {deletable.Count} items were not deleted.");
        }
        else
        {
            notifications.Show(gone.Count == 1 ? $"{gone[0].Title} moved to the Recycle Bin" : $"{gone.Count} items moved to the Recycle Bin");
        }
    }

    /// <summary>Forgets those of the items whose files are still gone (nothing on disk is touched).</summary>
    public void RemoveMissing(IEnumerable<LibraryItem> items) => library.Remove(items.Where(LibraryService.IsMissing).ToList());

    private bool IsBeingEdited(LibraryItem item, string what)
    {
        if (!editors.IsEditing(item.FilePath, item.ProjectPath))
        {
            return false;
        }

        notifications.Show($"{item.Title} is open in the editor. Close it before {what} it.");
        return true;
    }

    private bool Exists(LibraryItem item)
    {
        if (item.FilePath is { } path && File.Exists(path))
        {
            return true;
        }

        notifications.Show($"{item.Title} is no longer on disk.");
        return false;
    }

    private bool Try(string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Could not {What}", what);
            notifications.Show($"Could not {what}: {ex.Message}");
            return false;
        }
    }
}
