using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Grably.App.Imaging;
using Grably.Core.Recording;
using Grably.Platform.Recording;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Grably.App.Trim;

/// <summary>
/// Trims a recording: removes its ends and cut-out parts without touching the file, and exports the result as MP4
/// or GIF. Playback goes through <see cref="MediaElement"/>; whenever it is paused the picture is the exact frame,
/// decoded by ffmpeg, so what is on screen is precisely where an edit cuts.
/// </summary>
public partial class TrimWindow
{
    private const int PreviewWidth = 1600;
    private const int ThumbnailCount = 40;
    private const int ThumbnailHeight = 90;

    /// <summary>How often playback is followed; a cut is jumped over when the next tick would land past it.</summary>
    private static readonly TimeSpan PlaybackTick = TimeSpan.FromMilliseconds(15);

    private readonly TrimService _service;
    private readonly string _ffmpeg;
    private readonly ILogger _logger;
    private readonly bool _remember;
    private readonly FrameCache _frames = new(30);
    private readonly DispatcherTimer _playTimer;
    private readonly CancellationTokenSource _lifetime = new(); // stops this window's ffmpeg work when it closes
    private MediaInfo? _info;
    private EditHistory? _history;
    private bool _mediaReady;
    private bool _playing;
    private bool _closed;
    private int _wantedFrame = -1;
    private bool _isDecoding;
    private Task _decoding = Task.CompletedTask;
    private CancellationTokenSource? _exportCancel;
    private Task _export = Task.CompletedTask;
    private string? _exported;
    private bool _sessionEnding;

    /// <param name="remember">Keep the edits for the next time this video is opened (off for UI snapshots).</param>
    internal TrimWindow(TrimService service, string video, string ffmpeg, ILogger logger, bool remember = true)
    {
        InitializeComponent();
        _service = service;
        _ffmpeg = ffmpeg;
        _logger = logger;
        _remember = remember;
        VideoPath = video;
        Title = $"{Path.GetFileName(video)} - Grably trim";
        WindowTitleBar.Title = Title;

        _playTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = PlaybackTick };
        _playTimer.Tick += (_, _) => FollowPlayback();
        Timeline.Scrubbed += (_, time) =>
        {
            Pause();
            MoveTo(time);
        };
        Timeline.SelectionChanged += (_, _) => UpdateCommands();
        Timeline.EndsMoved += (_, ends) => Apply(Edits.WithEnds(ends.Start, ends.End));

        foreach (var speed in new[] { 0.5, 1, 1.5, 2 })
        {
            SpeedChoice.Items.Add(new ComboBoxItem { Content = $"{speed:0.#}x", Tag = speed });
        }

        SpeedChoice.SelectedIndex = 1;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>The recording being trimmed.</summary>
    public string VideoPath { get; }

    public bool IsExporting => !_export.IsCompleted;

    private EditList Edits => _history!.Current;

    private TimeSpan Playhead => Timeline.Playhead;

    /// <summary>
    /// Reads the video, the saved edits and the timeline pictures, and shows the first kept frame. Problems are shown
    /// in the window; this never throws.
    /// </summary>
    public async Task LoadAsync()
    {
        MediaInfo info;
        try
        {
            info = await VideoTools.ProbeAsync(_ffmpeg, VideoPath, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return; // closed while opening
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not open the video for trimming");
            PreviewMessage.Text = $"This video cannot be opened. {ex.Message}";
            return;
        }

        if (_closed)
        {
            return;
        }

        _info = info;
        // Saved edits come on top of the whole recording, so undo can still bring back everything that was cut.
        _history = new EditHistory(EditList.Whole(info.Duration));
        if (_remember && _service.LoadEdits(VideoPath, info.Duration) is { } saved)
        {
            _history.Apply(saved);
        }

        Timeline.FrameRate = info.FrameRate;
        Timeline.Edits = Edits;
        FillExportChoices(info);
        PreviewMessage.Text = string.Empty;
        foreach (var button in new UIElement[] { PreviousFrameButton, NextFrameButton, SaveFrameButton, ExportButton })
        {
            button.IsEnabled = true;
        }

        Media.Source = new Uri(VideoPath);
        Media.Pause(); // opens it, ready to play
        MoveTo(Edits.Start);
        UpdateCommands();
        UpdateLength();
        await LoadTimelinePicturesAsync(info);
        await _decoding;
    }

    private async Task LoadTimelinePicturesAsync(MediaInfo info)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"grably-trim-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            var strip = Path.Combine(folder, "thumbnails.png");
            var wave = Path.Combine(folder, "wave.png");
            var thumbnails = VideoTools.ThumbnailStripAsync(_ffmpeg, VideoPath, info.Duration, ThumbnailCount, ThumbnailHeight, strip, _lifetime.Token);
            var waveform = info.HasAudio ? VideoTools.WaveformAsync(_ffmpeg, VideoPath, 2400, 120, wave, _lifetime.Token) : Task.FromResult(false);
            if (await thumbnails && !_closed)
            {
                Timeline.SetThumbnails(await Task.Run(() => BitmapConversion.Decode(File.ReadAllBytes(strip))), ThumbnailCount);
            }

            if (await waveform && !_closed)
            {
                Timeline.Waveform = await Task.Run(() => BitmapConversion.Decode(File.ReadAllBytes(wave)));
            }
        }
        catch (OperationCanceledException)
        {
            // Closed meanwhile; ffmpeg was stopped.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not make the timeline pictures");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not delete {Folder}", folder);
            }
        }
    }

    private void FillExportChoices(MediaInfo info)
    {
        Mp4Size.Items.Add(new ComboBoxItem { Content = $"Original ({info.Width}x{info.Height})", Tag = null });
        foreach (var height in new[] { 1080, 720 }.Where(h => h < info.Height))
        {
            var (w, h) = OutputSize.For(info.Width, info.Height, height);
            Mp4Size.Items.Add(new ComboBoxItem { Content = $"{height}p ({w}x{h})", Tag = height });
        }

        Mp4Size.SelectedIndex = 0;

        var widths = new[] { 480, 640, 800, 1024 }.Where(w => w < info.Width).ToList();
        if (info.Width <= 1280)
        {
            widths.Add(info.Width);
        }

        foreach (var width in widths)
        {
            GifWidth.Items.Add(new ComboBoxItem { Content = width == info.Width ? $"Full width ({width} px)" : $"{width} px", Tag = width });
        }

        GifWidth.SelectedIndex = Math.Max(0, widths.FindLastIndex(w => w <= 800));
        foreach (var (rate, name) in new[] { (20, "Smooth"), (15, "Standard"), (10, "Small file") })
        {
            GifRate.Items.Add(new ComboBoxItem { Content = $"{name} ({rate} frames a second)", Tag = rate });
        }

        GifRate.SelectedIndex = 1;
    }

    // ---- Frames ----

    /// <summary>Moves the playhead (snapped to a frame) and shows that exact frame.</summary>
    private void MoveTo(TimeSpan time)
    {
        if (_info is not { } info)
        {
            return;
        }

        var last = VideoFrames.Start(Math.Max(0, VideoFrames.Index(info.Duration - TimeSpan.FromTicks(1), info.FrameRate)), info.FrameRate);
        var index = VideoFrames.Index(time > last ? last : time < TimeSpan.Zero ? TimeSpan.Zero : time, info.FrameRate);
        Timeline.Playhead = VideoFrames.Start(index, info.FrameRate);
        UpdateTime();
        ShowFrame(index);
    }

    private void Step(int frames)
    {
        if (_info is { } info)
        {
            Pause();
            MoveTo(VideoFrames.Start(VideoFrames.Index(Playhead, info.FrameRate) + frames, info.FrameRate));
        }
    }

    private void ShowFrame(int index)
    {
        _wantedFrame = index;
        if (_frames.TryGet(index, out var cached))
        {
            Display(cached);
            return;
        }

        if (!_isDecoding)
        {
            _isDecoding = true;
            _decoding = DecodeWantedAsync();
        }
    }

    /// <summary>
    /// Decodes the frame the playhead is on, then the next one asked for meanwhile, so a fast scrub shows the nearest
    /// frames it can instead of queueing every one it passed.
    /// </summary>
    private async Task DecodeWantedAsync()
    {
        try
        {
            while (!_closed && _info is { } info && !_frames.Contains(_wantedFrame))
            {
                var index = _wantedFrame;
                var bytes = await VideoTools.FrameAsync(_ffmpeg, VideoPath, index, info.FrameRate, PreviewWidth, _lifetime.Token);
                if (bytes is null)
                {
                    _logger.LogDebug("No picture for frame {Index}", index);
                    if (index == _wantedFrame)
                    {
                        return; // nothing newer asked for
                    }

                    continue;
                }

                var frame = await Task.Run(() => BitmapConversion.Decode(bytes));
                _frames.Add(index, frame);
                if (!_closed)
                {
                    Display(frame);
                }
            }

            if (_frames.TryGet(_wantedFrame, out var wanted))
            {
                Display(wanted);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed meanwhile; ffmpeg was stopped.
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or NotSupportedException or FileFormatException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not decode a frame for the preview");
        }
        finally
        {
            _isDecoding = false;
        }
    }

    private void Display(BitmapSource frame)
    {
        if (_playing)
        {
            return;
        }

        FrameImage.Source = frame;
        FrameImage.Visibility = Visibility.Visible;
    }

    // ---- Playback ----

    private void Play()
    {
        if (!_mediaReady || _history is null || _info is null || _playing)
        {
            return;
        }

        var from = Edits.Playable(Playhead);
        if (from is not { } start || Edits.End - start <= _info.FrameLength)
        {
            start = Edits.Start; // at the end: from the top
        }

        _playing = true;
        Media.Position = start;
        Media.SpeedRatio = SpeedChoice.SelectedItem is ComboBoxItem { Tag: double speed } ? speed : 1;
        Media.Play();
        FrameImage.Visibility = Visibility.Hidden;
        Timeline.Playhead = start;
        PlayButton.Icon = new SymbolIcon(SymbolRegular.Pause24);
        PlayButton.ToolTip = "Pause (Space)";
        _playTimer.Start();
    }

    /// <summary>Stops playback on the frame it reached (or <paramref name="at"/>) and shows that exact frame.</summary>
    private void Pause(TimeSpan? at = null)
    {
        if (!_playing)
        {
            return;
        }

        _playing = false;
        _playTimer.Stop();
        Media.Pause();
        PlayButton.Icon = new SymbolIcon(SymbolRegular.Play24);
        PlayButton.ToolTip = "Play the edited video (Space)";
        MoveTo(at ?? Media.Position);
    }

    /// <summary>During playback: follows the position and jumps over the removed parts.</summary>
    private void FollowPlayback()
    {
        if (!_playing || _info is null)
        {
            return;
        }

        // Jump when the next tick would already be past the end of this part, so removed frames never show.
        var position = Media.Position;
        var ahead = PlaybackTick * Media.SpeedRatio * 1.5;
        var part = Edits.Kept.FirstOrDefault(r => r.Contains(position));
        if (part.Duration <= TimeSpan.Zero || position + ahead >= part.End)
        {
            var next = Edits.Kept.FirstOrDefault(r => r.Start >= (part.Duration > TimeSpan.Zero ? part.End : position));
            if (next.Duration <= TimeSpan.Zero)
            {
                Pause(Edits.End - _info.FrameLength); // the end of the edited video: stop on its last frame
                return;
            }

            Media.Position = next.Start;
            position = next.Start;
        }

        Timeline.Playhead = position;
        UpdateTime();
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        PlayButton.IsEnabled = true;
    }

    private void Media_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _logger.LogWarning(e.ErrorException, "Windows cannot play the video in the trim editor");
        _mediaReady = false;
        _playing = false;
        _playTimer.Stop();
        PlayButton.IsEnabled = false;
        PlayButton.ToolTip = "Windows cannot play this video here; step through it frame by frame instead.";
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_info is { } info)
        {
            Pause(Edits.End - info.FrameLength);
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void NextFrame_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_playing && SpeedChoice.SelectedItem is ComboBoxItem { Tag: double speed })
        {
            Media.SpeedRatio = speed;
        }
    }

    // ---- Edits ----

    private void Apply(EditList next)
    {
        if (_history is null)
        {
            return;
        }

        if (next.Kept.Count == 0)
        {
            _service.Notify("Some of the video has to stay.");
            return;
        }

        _history.Apply(next);
        Timeline.Selection = null;
        EditsChanged();
    }

    private void EditsChanged()
    {
        Timeline.Edits = Edits;
        if (_remember)
        {
            _service.SaveEdits(VideoPath, Edits);
        }

        if (!Edits.IsKept(Playhead))
        {
            Pause();
            MoveTo(Edits.Playable(Playhead) ?? Edits.Kept[^1].Start);
        }

        UpdateCommands();
        UpdateLength();
    }

    private void CutSelection()
    {
        if (Timeline.Selection is { } selection)
        {
            Apply(Edits.Cut(selection));
        }
    }

    private void KeepSelection()
    {
        if (Timeline.Selection is { } selection)
        {
            Apply(Edits.Trim(selection.Start, selection.End));
        }
    }

    /// <summary>I and O: start or end the selection at the playhead.</summary>
    private void Mark(bool start)
    {
        if (_info is null)
        {
            return;
        }

        var current = Timeline.Selection ?? new TimeRange(Edits.Start, Edits.End);
        var time = Playhead;
        var range = start
            ? new TimeRange(time, time < current.End ? current.End : Edits.End)
            : new TimeRange(time > current.Start ? current.Start : Edits.Start, time + _info.FrameLength);
        Timeline.Selection = range.Duration > TimeSpan.Zero ? range : null;
        UpdateCommands();
    }

    private void Undo()
    {
        if (_history is { CanUndo: true })
        {
            _history.Undo();
            Timeline.Selection = null;
            EditsChanged();
        }
    }

    private void Redo()
    {
        if (_history is { CanRedo: true })
        {
            _history.Redo();
            Timeline.Selection = null;
            EditsChanged();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Cut_Click(object sender, RoutedEventArgs e) => CutSelection();

    private void Keep_Click(object sender, RoutedEventArgs e) => KeepSelection();

    // ---- The frame in the editor ----

    private async void SaveFrame_Click(object sender, RoutedEventArgs e) => await EditFrameAsync();

    /// <summary>Opens the frame on screen, at full size, in the image editor.</summary>
    private async Task EditFrameAsync()
    {
        if (_info is not { } info)
        {
            return;
        }

        Pause();
        SaveFrameButton.IsEnabled = false;
        try
        {
            var bytes = await VideoTools.FrameAsync(_ffmpeg, VideoPath, VideoFrames.Index(Playhead, info.FrameRate), info.FrameRate, cancellationToken: _lifetime.Token);
            if (bytes is null)
            {
                _service.Notify("That frame could not be read.");
                return;
            }

            _service.EditFrame(await Task.Run(() => BitmapConversion.Decode(bytes)));
        }
        catch (OperationCanceledException)
        {
            // Closed meanwhile.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not read the frame for the editor");
            _service.Notify("That frame could not be read.");
        }
        finally
        {
            SaveFrameButton.IsEnabled = true;
        }
    }

    // ---- Export ----

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (Mp4Options is null || GifOptions is null)
        {
            return; // still loading the XAML
        }

        var gif = GifChoice.IsChecked == true;
        Mp4Options.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
        GifOptions.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        _export = ExportAsync();
        await _export;
    }

    private async Task ExportAsync()
    {
        if (_info is null || _history is null)
        {
            return;
        }

        var settings = GifChoice.IsChecked == true
            ? new ExportSettings(
                ExportFormat.Gif,
                GifFrameRate: GifRate.SelectedItem is ComboBoxItem { Tag: int rate } ? rate : 15,
                GifWidth: GifWidth.SelectedItem is ComboBoxItem { Tag: int width } ? width : 800)
            : new ExportSettings(ExportFormat.Mp4, MaxHeight: Mp4Size.SelectedItem is ComboBoxItem { Tag: int height } ? height : null);
        string? output = null;
        using var cancel = new CancellationTokenSource();
        _exportCancel = cancel;
        ExportButton.IsEnabled = false;
        ExportResultPanel.Visibility = Visibility.Collapsed;
        ExportProgressPanel.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ExportProgressText.Text = "Exporting...";
        var progress = new Progress<double>(p =>
        {
            ExportProgress.Value = p;
            ExportProgressText.Text = $"Exporting... {(int)(p * 100)}%";
        });

        string? error;
        var cancelled = false;
        try
        {
            output = _service.ExportPathFor(VideoPath, settings.Format);
            error = await _service.Exporter(_ffmpeg).ExportAsync(VideoPath, Edits, settings, _service.Quality, output, progress, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            (error, cancelled) = (null, true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Export failed");
            error = $"The export failed: {ex.Message}";
        }
        finally
        {
            _exportCancel = null;
            ExportProgressPanel.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = !_closed;
        }

        if (_closed)
        {
            return;
        }

        if (cancelled || error is not null)
        {
            ShowExportResult(cancelled ? "Export cancelled." : error!, succeeded: false);
            return;
        }

        _exported = output!;
        ShowExportResult($"Saved as {Path.GetFileName(output)}", succeeded: true);
        await _service.AddToLibraryAsync(_ffmpeg, output!, settings.Format);
    }

    private void ShowExportResult(string text, bool succeeded)
    {
        ExportResultText.Text = text;
        ExportResultButtons.Visibility = succeeded ? Visibility.Visible : Visibility.Collapsed;
        ExportResultPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Stops a running export (the unfinished file is removed) and waits for it to wind down.</summary>
    public async Task StopExportAsync()
    {
        _exportCancel?.Cancel();
        await _export;
    }

    /// <summary>Windows is signing out: stop any export now (it leaves only a .part file) and close without asking.</summary>
    public void SessionEnding()
    {
        _sessionEnding = true;
        _exportCancel?.Cancel();
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancel?.Cancel();

    private void OpenExport_Click(object sender, RoutedEventArgs e) => OpenShell(_exported, select: false);

    private void ShowExport_Click(object sender, RoutedEventArgs e) => OpenShell(_exported, select: true);

    private void OpenShell(string? file, bool select)
    {
        if (file is null || !File.Exists(file))
        {
            _service.Notify("The exported file is no longer there.");
            return;
        }

        try
        {
            Process.Start(select
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true }
                : new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the export");
            _service.Notify($"Could not open {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    // ---- Labels and buttons ----

    private void UpdateTime()
    {
        if (_info is { } info)
        {
            TimeText.Text = $"{VideoFrames.Format(Playhead)} / {VideoFrames.Format(info.Duration)}";
        }
    }

    private void UpdateLength()
    {
        if (_info is null)
        {
            return;
        }

        var parts = Edits.Kept.Count;
        EditedLengthText.Text = Edits.IsWhole
            ? $"The whole recording, {VideoFrames.Format(Edits.Duration)}"
            : $"{VideoFrames.Format(Edits.Duration)} of {VideoFrames.Format(Edits.SourceDuration)} kept{(parts > 1 ? $", in {parts} parts" : "")}";
        GifHint.Text = Edits.Duration > TimeSpan.FromSeconds(30)
            ? $"This GIF would run for {Edits.Duration.TotalSeconds:0} seconds. GIFs get large quickly, so MP4 suits long clips better."
            : "GIFs get large quickly; short clips work best.";
    }

    private void UpdateCommands()
    {
        var loaded = _history is not null;
        UndoButton.IsEnabled = _history is { CanUndo: true };
        RedoButton.IsEnabled = _history is { CanRedo: true };
        CutButton.IsEnabled = loaded && Timeline.Selection is not null;
        KeepButton.IsEnabled = loaded && Timeline.Selection is not null;
    }

    // ---- Keys and closing ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_history is null || e.OriginalSource is ComboBox or ComboBoxItem or System.Windows.Controls.TextBox)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var fps = (int)Math.Round(_info?.FrameRate ?? 30);
        Action? action = (e.Key, ctrl) switch
        {
            (Key.Space, false) => () => TogglePlay(),
            (Key.Left, false) => () => Step(shift ? -fps : -1),
            (Key.Right, false) => () => Step(shift ? fps : 1),
            (Key.Home, false) => () => GoTo(Edits.Start),
            (Key.End, false) => () => GoTo(Edits.End - _info!.FrameLength),
            (Key.Delete or Key.Back, false) => () => CutSelection(),
            (Key.K, false) => () => KeepSelection(),
            (Key.I, false) => () => Mark(start: true),
            (Key.O, false) => () => Mark(start: false),
            (Key.Escape, false) when Timeline.Selection is not null => () => ClearSelection(),
            (Key.Z, true) when shift => () => Redo(),
            (Key.Z, true) => () => Undo(),
            (Key.Y, true) => () => Redo(),
            (Key.E, true) => () => _ = EditFrameAsync(),
            _ => null,
        };
        if (action is not null)
        {
            action();
            e.Handled = true;
        }
    }

    private void GoTo(TimeSpan time)
    {
        Pause();
        MoveTo(time);
    }

    private void ClearSelection()
    {
        Timeline.Selection = null;
        UpdateCommands();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (IsExporting && !_sessionEnding
            && MessageBox.Show(this, "An export is still running. Stop it and close?", "Grably", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _exportCancel?.Cancel(); // ffmpeg stops at once; the unfinished file is removed
        _lifetime.Cancel(); // and so do frame decodes and the timeline pictures, which keep the video open
        _playTimer.Stop();
        Media.Close();
        Media.Source = null;
    }

#if DEBUG
    /// <summary>UI snapshots: loads, makes a few edits and a selection, and waits for every picture.</summary>
    internal async Task PrepareSampleAsync()
    {
        await LoadAsync();
        Func<double, TimeSpan> s = TimeSpan.FromSeconds;
        Apply(Edits.WithEnds(s(1), s(11)));
        Apply(Edits.Cut(new TimeRange(s(4), s(6))));
        Timeline.Selection = new TimeRange(s(7.5), s(9));
        UpdateCommands();
        MoveTo(s(8.2));
        await _decoding;
    }
#endif

    /// <summary>The most recently shown preview frames, by frame number.</summary>
    private sealed class FrameCache(int capacity)
    {
        private readonly Dictionary<int, LinkedListNode<(int Index, BitmapSource Frame)>> _byIndex = [];
        private readonly LinkedList<(int Index, BitmapSource Frame)> _recent = new();

        public bool Contains(int index) => _byIndex.ContainsKey(index);

        public bool TryGet(int index, [NotNullWhen(true)] out BitmapSource? frame)
        {
            if (_byIndex.TryGetValue(index, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                frame = node.Value.Frame;
                return true;
            }

            frame = null;
            return false;
        }

        public void Add(int index, BitmapSource frame)
        {
            if (_byIndex.ContainsKey(index))
            {
                return;
            }

            _byIndex[index] = _recent.AddFirst((index, frame));
            if (_recent.Count > capacity)
            {
                _byIndex.Remove(_recent.Last!.Value.Index);
                _recent.RemoveLast();
            }
        }
    }
}
