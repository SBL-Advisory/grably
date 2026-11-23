using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Grably.App.Capture;
using Grably.App.Library;
using Grably.App.Shell;
using Grably.Core;
using Grably.Core.Capture;
using Grably.Core.Recording;
using Grably.Core.Settings;
using Grably.Platform.Capture;
using Grably.Platform.Display;
using Grably.Platform.Hotkeys;
using Grably.Platform.Recording;
using Grably.Platform.Shell;
using Microsoft.Extensions.Logging;
using MessageBox = System.Windows.MessageBox;

namespace Grably.App.Recording;

public enum RecordingState
{
    Idle,

    /// <summary>Picking the area, the pre-record bar, or the countdown.</summary>
    Preparing,

    Recording,
    Paused,

    /// <summary>Stopping: the last frames, the audio mix, saving.</summary>
    Finishing,
}

/// <summary>
/// The whole recording flow, from the Record hotkey to the saved video. Picks the area (a window that is clicked is
/// followed even when it moves), shows the pre-record bar and the countdown, runs <see cref="ScreenRecorder"/> with
/// the control bar and the area's outline (both left out of the video), then saves the MP4 to the videos folder and
/// adds it to the library. Interrupted recordings are offered back at the next start. Runs on the UI thread.
/// </summary>
public sealed class RecordingController(
    ScreenshotService screenshots, SettingsStore settings, AppPaths paths, HotkeyController hotkeys, GlobalHotkeyService globalHotkeys,
    TrayIconController tray, LibraryService library, UserNotifications notifications, ILogger<RecordingController> logger)
{
    private static readonly Core.Hotkeys.HotkeyGesture Escape = Core.Hotkeys.HotkeyGesture.Parse("Esc");

    private EncoderProbe? _probe;
    private ScreenRecorder? _recorder;
    private PreRecordWindow? _preRecord;
    private bool _stopping;
    private RecordingControlBar? _bar;
    private RegionFrameWindow? _frame;
    private CaptureTarget? _target;
    private WindowInfo? _source;
    private DateTime _started;

    public RecordingState State { get; private set; } = RecordingState.Idle;

    public event EventHandler? StateChanged;

    /// <summary>The Record hotkey and button: start when idle, stop and save when recording.</summary>
    public async Task ToggleAsync()
    {
        switch (State)
        {
            case RecordingState.Idle:
                await StartAsync();
                break;
            case RecordingState.Recording or RecordingState.Paused:
                await StopAsync(save: true);
                break;
            case RecordingState.Finishing:
                notifications.Show("Still saving the last recording.");
                break;
            default:
                logger.LogInformation("Record pressed while {State}; ignored", State);
                break;
        }
    }

    public void TogglePause()
    {
        // A pause hotkey that arrives while stopping or preparing must not touch the recorder or the state.
        if (_recorder is null || _stopping || State is not (RecordingState.Recording or RecordingState.Paused))
        {
            return;
        }

        if (_recorder.IsPaused)
        {
            _recorder.Resume();
            SetState(RecordingState.Recording);
        }
        else
        {
            _recorder.Pause();
            SetState(RecordingState.Paused);
        }

        UpdateBar();
    }

    /// <summary>
    /// Offers recordings that were interrupted (Grably or Windows stopped mid-recording) for saving. Nothing is ever
    /// deleted without the explicit Delete button, and deleted ones go to the Recycle Bin.
    /// </summary>
    public async Task OfferRecoveryAsync()
    {
        var unsaved = UnsavedSessions();
        if (unsaved.Count == 0)
        {
            return;
        }

        var newest = unsaved.Max(s => s.Session.StartedUtc).ToLocalTime();
        var one = unsaved.Count == 1;
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Grably",
            Content = $"Grably found {(one ? "a recording" : $"{unsaved.Count} recordings")} that {(one ? "was" : "were")} not saved because "
                + $"recording was interrupted (the last one started {newest:g}).",
            PrimaryButtonText = one ? "Save it" : "Save them",
            SecondaryButtonText = one ? "Delete it" : "Delete them",
            CloseButtonText = "Later",
        };
        var answer = await dialog.ShowDialogAsync();
        foreach (var (session, folder) in unsaved)
        {
            if (answer == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                await SaveAsync(session, folder, session.StartedUtc.ToLocalTime(), duration: null, source: null);
            }
            else if (answer == Wpf.Ui.Controls.MessageBoxResult.Secondary)
            {
                RecycleSession(folder);
            }
        }
    }

    private async Task StartAsync()
    {
        SetState(RecordingState.Preparing);
        try
        {
            PickedArea? picked = null;
            await screenshots.RunExclusiveAsync(async () =>
                picked = await screenshots.PickAreaAsync("Drag the area to record, or click a window or a screen. Esc cancels."));
            if (picked is null)
            {
                SetState(RecordingState.Idle);
                return;
            }

            var monitors = MonitorService.GetMonitors();
            var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(picked.Area.Center)) ?? monitors[0];
            _source = picked.Source;
            _target = picked.Source is { } window ? CaptureTarget.ForWindow(window)
                : monitors.FirstOrDefault(m => m.Bounds == picked.Area) is { } whole ? CaptureTarget.ForMonitor(whole)
                : CaptureTarget.ForRegion(picked.Area, monitors);
            var area = picked.Area.Intersect(monitor.Bounds);
            if (_target.Kind == CaptureTargetKind.Monitor && _target.Crop is not null && area != picked.Area)
            {
                notifications.Show("A recording covers one screen; the part of the area on the other screen is left out.");
            }

            var options = settings.Current.Recording;
            _frame = new RegionFrameWindow(area);
            _frame.Show();
            _preRecord = new PreRecordWindow(
                area, monitor.WorkArea, new PreRecordChoice(options.RecordSystemAudio, options.RecordMicrophone, options.FrameRate), options.MicrophoneDeviceId);
            var choice = await _preRecord.ShowAsync();
            _preRecord = null;
            if (choice is null)
            {
                CloseChrome();
                SetState(RecordingState.Idle);
                return;
            }

            Remember(choice);
            var ffmpeg = FfmpegLocator.Find();
            _probe ??= new EncoderProbe(ffmpeg, logger);
            var (width, height) = OutputSize.For(area.Width, area.Height, options.MaxHeight);
            var warmUp = _probe.ChooseAsync(width, height); // during the countdown, so recording starts the moment it ends
            if (options.ShowCountdown && !await CountdownAsync(area))
            {
                CloseChrome();
                SetState(RecordingState.Idle);
                return;
            }

            await warmUp;
            _started = DateTime.Now;
            _recorder = await ScreenRecorder.StartAsync(
                new RecorderOptions
                {
                    Target = _target,
                    FrameRate = choice.FrameRate,
                    Quality = options.Quality,
                    MaxHeight = options.MaxHeight,
                    SystemAudio = choice.SystemAudio,
                    SystemAudioDeviceId = options.SystemAudioDeviceId,
                    Microphone = choice.Microphone,
                    MicrophoneDeviceId = options.MicrophoneDeviceId,
                    Ffmpeg = ffmpeg,
                    SessionsFolder = paths.SessionsFolder,
                },
                _probe,
                logger);
            foreach (var warning in _recorder.Warnings)
            {
                notifications.Show(warning);
            }

            _recorder.Warning += (_, message) => Application.Current.Dispatcher.BeginInvoke(() => notifications.Show(message));

            _recorder.SourceClosed += (_, _) => Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                notifications.Show("The window being recorded was closed, so the recording was saved.");
                await StopAsync(save: true);
            });
            _recorder.Failed += (_, error) => Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                notifications.Show("Recording stopped because of a problem; what was recorded is being saved.");
                await StopAsync(save: true);
            });

            if (_target.Crop is null)
            {
                CloseFrame(); // a whole screen or a window needs no outline
            }

            _bar = new RecordingControlBar(area, monitor.WorkArea, choice.Microphone && _recorder.Warnings.Count == 0);
            _bar.PauseClicked += (_, _) => TogglePause();
            _bar.MuteClicked += (_, _) =>
            {
                _recorder.MicrophoneMuted = !_recorder.MicrophoneMuted;
                UpdateBar();
            };
            _bar.StopClicked += async (_, _) => await StopAsync(save: true);
            _bar.DiscardClicked += async (_, _) =>
            {
                // Paused while asking, so the question is not in the video (and nothing is lost if the answer is No).
                var wasPaused = _recorder.IsPaused;
                _recorder.Pause();
                if (MessageBox.Show("Discard this recording? It will not be saved.", "Grably", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    await StopAsync(save: false);
                }
                else if (!wasPaused && _recorder is { } still)
                {
                    still.Resume();
                }
            };
            _bar.Show();
            _bar.Ticker(UpdateBar);
            hotkeys.SetRecordingActive(true);
            tray.SetRecording(true);
            SetState(RecordingState.Recording);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Recording could not start");
            notifications.Show($"Recording could not start: {ex.Message}");
            CloseChrome();
            if (_recorder is not null)
            {
                // It started before something else failed: never leave it recording without any controls.
                SetState(RecordingState.Recording);
                await StopAsync(save: true);
                return;
            }

            hotkeys.SetRecordingActive(false);
            tray.SetRecording(false);
            SetState(RecordingState.Idle);
        }
    }

    /// <summary>Exit was chosen: a running recording is stopped and saved first, if the user agrees. False to stay.</summary>
    public async Task<bool> ConfirmExitAsync()
    {
        if (State is RecordingState.Idle)
        {
            return true;
        }

        if (State is RecordingState.Preparing or RecordingState.Finishing)
        {
            notifications.Show(State == RecordingState.Finishing ? "Still saving the last recording." : "Finish or cancel the recording first.");
            return false;
        }

        if (MessageBox.Show("A recording is running. Stop it, save it and quit?", "Grably", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return false;
        }

        await StopAsync(save: true);
        return true;
    }

    /// <summary>
    /// Windows is signing out or shutting down: close the recording files properly (there is no time to save the MP4),
    /// so the recording is offered back, complete, at the next start.
    /// </summary>
    public void StopForSessionEnd()
    {
        if (_recorder is not { } recorder)
        {
            return;
        }

        _recorder = null;
        try
        {
            Task.Run(recorder.StopAsync).Wait(TimeSpan.FromSeconds(8));
        }
        catch (AggregateException ex)
        {
            logger.LogWarning(ex, "Closing the recording at sign-out failed; it will be recovered next time");
        }
    }

    private async Task StopAsync(bool save)
    {
        if (_recorder is not { } recorder || _stopping)
        {
            return;
        }

        _stopping = true;
        SetState(RecordingState.Finishing);
        CloseChrome();
        hotkeys.SetRecordingActive(false);
        tray.SetRecording(false);
        try
        {
            var result = await recorder.StopAsync();
            if (!save)
            {
                DeleteSession(result.SessionFolder);
                logger.LogInformation("Recording discarded");
                return;
            }

            await SaveAsync(result.Session, result.SessionFolder, _started, result.Duration, _source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Stopping the recording failed");
            notifications.Show($"The recording could not be saved: {ex.Message}. It will be offered again when Grably starts.");
        }
        finally
        {
            _recorder = null;
            _stopping = false;
            SetState(RecordingState.Idle);
        }
    }

    /// <summary>Muxes the session into the videos folder, adds it to the library and removes the session.</summary>
    private async Task SaveAsync(RecordingSession session, string folder, DateTime started, TimeSpan? duration, WindowInfo? source)
    {
        var ffmpeg = FfmpegLocator.Find();
        var videos = settings.Current.General.VideosFolder ?? paths.DefaultVideosFolder;
        Directory.CreateDirectory(videos);
        var file = CaptureNaming.UniquePath(videos, started, "mp4");
        if (await ScreenRecorder.SaveAsMp4Async(ffmpeg, session, folder, file) is { } error)
        {
            logger.LogError("Saving the recording failed: {Error}", error);
            notifications.Show("The recording could not be saved. It will be offered again when Grably starts.");
            return;
        }

        // Saved: say so in the session before anything else, so a session that cannot be removed is never saved twice.
        (session with { OutputPath = file }).Save(folder);
        duration ??= await FfmpegLocator.DurationAsync(ffmpeg, file);
        var frame = await FirstFrameAsync(ffmpeg, file);
        library.AddRecording(file, duration ?? TimeSpan.Zero, session.Width, session.Height, frame, source?.ProcessName, source?.Title, started);
        DeleteSession(folder);
        logger.LogInformation("Recording saved ({Duration}, {Width}x{Height})", duration, session.Width, session.Height);
        notifications.Show($"Recording saved as {Path.GetFileName(file)}");
    }

    private async Task<bool> CountdownAsync(Core.Geometry.PixelRect area)
    {
        using var cancel = new CancellationTokenSource();
        using var escape = globalHotkeys.RegisterTemporary(Escape, () => cancel.Cancel());
        return await new CountdownWindow(area).RunAsync(3, cancel.Token);
    }

    private void Remember(PreRecordChoice choice)
    {
        var current = settings.Current;
        if (current.Recording.RecordSystemAudio == choice.SystemAudio && current.Recording.RecordMicrophone == choice.Microphone
            && current.Recording.FrameRate == choice.FrameRate)
        {
            return;
        }

        var changed = current.Clone();
        changed.Recording.RecordSystemAudio = choice.SystemAudio;
        changed.Recording.RecordMicrophone = choice.Microphone;
        changed.Recording.FrameRate = choice.FrameRate;
        try
        {
            settings.Save(changed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remember the recording choices");
        }
    }

    private void UpdateBar()
    {
        if (_recorder is { } recorder)
        {
            _bar?.Update(recorder.Elapsed, recorder.IsPaused, recorder.MicrophoneMuted);
        }
    }

    private void CloseChrome()
    {
        _preRecord?.Cancel();
        _preRecord = null;
        _bar?.Close();
        _bar = null;
        CloseFrame();
    }

    private void CloseFrame()
    {
        _frame?.Close();
        _frame = null;
    }

    private void SetState(RecordingState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Session folders with a video but no saved MP4 (the app or PC stopped mid-recording, or saving failed).</summary>
    private List<(RecordingSession Session, string Folder)> UnsavedSessions()
    {
        var found = new List<(RecordingSession, string)>();
        if (!Directory.Exists(paths.SessionsFolder))
        {
            return found;
        }

        foreach (var folder in Directory.GetDirectories(paths.SessionsFolder))
        {
            var video = Path.Combine(folder, RecordingSession.VideoFileName);
            if (RecordingSession.Load(folder) is { OutputPath: null } session && File.Exists(video) && new FileInfo(video).Length > 0)
            {
                found.Add((session, folder));
            }
        }

        return found;
    }

    /// <summary>The user chose Delete: the session's files go to the Recycle Bin, so the choice can be undone.</summary>
    private void RecycleSession(string folder)
    {
        if (!RecycleBin.Send(Directory.GetFiles(folder)))
        {
            logger.LogWarning("The Recycle Bin did not take the recording session {Folder}", folder);
        }

        if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0)
        {
            DeleteSession(folder);
        }
    }

    private void DeleteSession(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete the recording session {Folder}", folder);
        }
    }

    /// <summary>A frame from early in the video, for the library thumbnail.</summary>
    private async Task<BitmapSource?> FirstFrameAsync(string ffmpeg, string video)
    {
        var png = Path.Combine(Path.GetTempPath(), $"grably-thumb-{Guid.NewGuid():N}.png");
        try
        {
            var (code, _) = await FfmpegProcess.RunAsync(ffmpeg, FfmpegArguments.ExtractFrame(video, 0.5, png), TimeSpan.FromSeconds(20));
            if (code != 0 || !File.Exists(png))
            {
                return null;
            }

            await using var stream = File.OpenRead(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            logger.LogDebug(ex, "No thumbnail for the recording");
            return null;
        }
        finally
        {
            File.Delete(png);
        }
    }
}
