using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Grably.App.Editor;
using Grably.App.Imaging;
using Grably.App.Library;
using Grably.App.Shell;
using Grably.Core;
using Grably.Core.Capture;
using Grably.Core.Library;
using Grably.Core.Recording;
using Grably.Core.Settings;
using Grably.Platform.Recording;
using Microsoft.Extensions.Logging;

namespace Grably.App.Trim;

/// <summary>
/// Opens trim windows (one per video) and does the work behind them: keeping each video's edits, exporting, adding
/// exports to the library and sending a frame to the image editor.
/// </summary>
public sealed class TrimService(
    SettingsStore settings, AppPaths paths, LibraryService library, EditorService editors, UserNotifications notifications,
    ILogger<TrimService> logger)
{
    private readonly List<TrimWindow> _open = [];
    private EncoderProbe? _probe;

    /// <summary>Whether <paramref name="video"/> is open in a trim window (which keeps the file open for playback).</summary>
    public bool IsOpen(string? video) => video is not null && _open.Any(w => SamePath(w.VideoPath, video));

    /// <summary>Opens the trim editor for a video, or brings its window forward when it is already open.</summary>
    public void Open(string video)
    {
        if (_open.FirstOrDefault(w => SamePath(w.VideoPath, video)) is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        if (!File.Exists(video))
        {
            notifications.Show($"{Path.GetFileName(video)} is no longer on disk.");
            return;
        }

        string ffmpeg;
        try
        {
            ffmpeg = FfmpegLocator.Find();
        }
        catch (FileNotFoundException ex)
        {
            logger.LogError(ex, "Trimming needs ffmpeg");
            notifications.Show("Trimming needs ffmpeg, which is missing from this installation of Grably.");
            return;
        }

        logger.LogInformation("Opening the trim editor");
        var window = new TrimWindow(this, video, ffmpeg, logger);
        _open.Add(window);
        window.Closed += (_, _) => _open.Remove(window);
        window.Show();
        _ = window.LoadAsync();
    }

    /// <summary>Before quitting: running exports are stopped (their unfinished files removed) only if the user agrees.</summary>
    public async Task<bool> ConfirmExitAsync()
    {
        var exporting = _open.Where(w => w.IsExporting).ToList();
        if (exporting.Count == 0)
        {
            return true;
        }

        if (MessageBox.Show("An export is still running. Stop it and quit?", "Grably", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return false;
        }

        await Task.WhenAll(exporting.Select(w => w.StopExportAsync()));
        return true;
    }

    /// <summary>Windows is signing out: running exports stop without a prompt (sign-out would not wait for one).</summary>
    public void SessionEnding()
    {
        foreach (var window in _open)
        {
            window.SessionEnding();
        }
    }

    internal EditList? LoadEdits(string video, TimeSpan duration)
    {
        try
        {
            return TrimEdits.Load(paths.TrimsFolder, video, duration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the saved edits of a video");
            return null;
        }
    }

    internal void SaveEdits(string video, EditList edits)
    {
        try
        {
            TrimEdits.Save(paths.TrimsFolder, video, edits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The edits still apply in this window and its exports; only reopening later would not find them.
            logger.LogWarning(ex, "Could not save the edits of a video");
        }
    }

    /// <summary>The probe is shared, so each output size is tested once per run of the app.</summary>
    internal VideoExporter Exporter(string ffmpeg) => new(ffmpeg, _probe ??= new EncoderProbe(ffmpeg, logger), logger);

    internal VideoQuality Quality => settings.Current.Recording.Quality;

    internal string ExportPathFor(string video, ExportFormat format) =>
        CaptureNaming.ExportPath(video, format == ExportFormat.Gif ? "gif" : "mp4");

    internal void Notify(string message) => notifications.Show(message);

    internal void EditFrame(BitmapSource frame) => editors.OpenImage(frame);

    /// <summary>A finished export joins the library, with a thumbnail of its first frame.</summary>
    internal async Task AddToLibraryAsync(string ffmpeg, string file, ExportFormat format)
    {
        try
        {
            var info = await VideoTools.ProbeAsync(ffmpeg, file);
            var bytes = await VideoTools.FrameAsync(ffmpeg, file, 0, info.FrameRate, LibraryService.ThumbnailWidth * 2);
            var frame = bytes is null ? null : await Task.Run(() => BitmapConversion.Decode(bytes));
            library.AddRecording(
                file, info.Duration, info.Width, info.Height, frame, null, null, DateTime.Now,
                format == ExportFormat.Gif ? LibraryItemKind.Gif : LibraryItemKind.Recording);
            logger.LogInformation("Export added to the library ({Format}, {Duration})", format, info.Duration);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The export itself is saved and shown in the window; only its library entry is missing.
            logger.LogWarning(ex, "Could not add the export to the library");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
