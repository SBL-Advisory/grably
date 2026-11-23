using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Grably.App.Library;
using Grably.Core.Library;

namespace Grably.App.ViewModels;

/// <summary>One tile in the library grid.</summary>
public sealed partial class LibraryItemViewModel(LibraryItem item) : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public LibraryItem Item { get; } = item;

    public string Title => Item.Title;

    /// <summary>The file (or, for a project-only item, the project) is gone. Set by <see cref="SetFileState"/>.</summary>
    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    /// <summary>The annotations can be reopened (the project exists). Set by <see cref="SetFileState"/>.</summary>
    [ObservableProperty]
    public partial bool IsEditable { get; private set; }

    /// <summary>What the disk says, checked in the background because a network folder can be slow.</summary>
    public void SetFileState(bool missing, bool editable)
    {
        IsMissing = missing;
        IsEditable = editable;
    }

    /// <summary>"Today 15:30 · 1920 x 1080 · Code".</summary>
    public string Details
    {
        get
        {
            var local = Item.CreatedUtc.ToLocalTime();
            var when = local.Date == DateTime.Today ? $"Today {local:t}"
                : local.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {local:t}"
                : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
            var parts = new List<string> { when };
            if (Item.Width > 0)
            {
                parts.Add($"{Item.Width} × {Item.Height}");
            }

            if (Item.DurationMs is { } ms)
            {
                var duration = TimeSpan.FromMilliseconds(ms);
                parts.Add(duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss"));
            }

            if (!string.IsNullOrEmpty(Item.SourceApp))
            {
                parts.Add(Item.SourceApp);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Loaded on first use, so only the tiles on screen read their thumbnail (the grid is virtualized).</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = Load(Item.ThumbnailPath) ?? LoadSmall(Item.FilePath);
            }

            return _thumbnail;
        }
    }

    /// <summary>The image itself, decoded small: items found on disk by a library rebuild have no thumbnail.</summary>
    private static BitmapImage? LoadSmall(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path); // not a URI: file names may contain '#'
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = LibraryService.ThumbnailWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }
    }

    private static BitmapImage? Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read now and release the file
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
