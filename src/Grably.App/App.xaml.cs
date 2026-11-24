using System.Windows;
using System.Windows.Threading;
using Grably.App.Shell;
using Grably.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Grably.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private readonly LaunchOptions _options;
    private readonly AppPaths _paths;
    private readonly ILogger<App> _logger;

    public App(IServiceProvider services, LaunchOptions options, AppPaths paths, ILogger<App> logger)
    {
        _services = services;
        _options = options;
        _paths = paths;
        _logger = logger;

        // Grably lives in the tray: closing the last window must not end the process.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Sign-out or shutdown closes windows without asking; keep unsaved annotations in their projects.
        SessionEnding += (_, _) =>
        {
            _services.GetService<Recording.RecordingController>()?.StopForSessionEnd();
            _services.GetService<Editor.EditorService>()?.SaveForSessionEnd();
            _services.GetService<Trim.TrimService>()?.SessionEnding();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _logger.LogCritical(e.ExceptionObject as Exception, "Unhandled error (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger.LogError(e.Exception, "Unobserved task error");
            e.SetObserved();
        };
    }

    /// <summary>Another Grably.exe was started while this one runs (it forwards its arguments and exits).</summary>
    public void OnLaunchedAgain(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        if (options.Background)
        {
            return; // e.g. a second start-with-Windows launch: nothing to show
        }

        Dispatcher.BeginInvoke(() => OpenRequested(options));
    }

    /// <summary>
    /// A different Grably.exe asks this one to close (see <see cref="RunningCopy"/>): the same questions as Exit, on
    /// the UI thread. Called on a background thread.
    /// </summary>
    public Task<bool> OnCloseRequested(Platform.Shell.CloseRequest request) =>
        Dispatcher.InvokeAsync(() => _services.GetRequiredService<AppCommands>().ConfirmCloseForAnotherCopyAsync(request))
            .Task.Unwrap();

    /// <summary>The other launch has its answer: quit so that it can start.</summary>
    public void CloseForAnotherCopy() => Dispatcher.BeginInvoke(() => _services.GetRequiredService<AppCommands>().Quit());

    /// <summary>Opens the files a launch asked for, or the Library when there are none.</summary>
    private void OpenRequested(LaunchOptions options)
    {
        if (options.Files.Count == 0)
        {
            _services.GetRequiredService<AppCommands>().OpenLibrary();
            return;
        }

        var editors = _services.GetRequiredService<Editor.EditorService>();
        foreach (var file in options.Files)
        {
            editors.OpenFile(file);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _services.GetRequiredService<ThemeController>().Initialize(_options.Snapshot?.Theme);

#if DEBUG
            if (_options.Snapshot is { } snapshot)
            {
                Shutdown(Diagnostics.UiSnapshot.Run(snapshot, _services, _logger));
                return;
            }
#endif

            FollowStartupEntry();

            // Start remembering the user's foreground window before the tray menu can be used (needs the UI thread).
            _services.GetRequiredService<Platform.Capture.ForegroundWindowTracker>();
            _services.GetRequiredService<TrayIconController>().Show();
            _services.GetRequiredService<HotkeyController>().Start();
            _services.GetRequiredService<Text.TextIndexer>().Start();
            var updates = _services.GetRequiredService<UpdateService>();
            updates.RestartRequested += (_, _) => _services.GetRequiredService<AppCommands>().RestartToUpdate();
            _ = updates.CheckRegularlyAsync();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, async () =>
                await _services.GetRequiredService<Recording.RecordingController>().OfferRecoveryAsync());
            _logger.LogInformation("Ready");

            if (!_options.Background)
            {
                OpenRequested(_options);
            }
        }
        catch (Exception ex)
        {
            // A half-started Grably would hold the single-instance lock with no tray icon or hotkeys, and every later
            // launch would forward to it. Exit instead so the next launch starts clean.
            _logger.LogCritical(ex, "Startup failed");
            if (_options.Snapshot is null)
            {
                ShowError($"Grably could not start: {ex.Message}");
            }

            Shutdown(1);
        }
    }

    /// <summary>
    /// When Grably starts with Windows but the entry names another Grably.exe (another build folder, a portable or
    /// moved copy), a Release build points it at itself, so the copy the user last ran is the one that starts at
    /// sign-in and takes later launches. A Debug build only logs it, so a development build never takes over an
    /// installed Grably's sign-in.
    /// </summary>
    private void FollowStartupEntry()
    {
#if DEBUG
        const bool rewrite = false;
#else
        const bool rewrite = true;
#endif
        var executable = Platform.Shell.ExecutableInfo.Current.Path;
        if (executable.Length == 0)
        {
            return;
        }

        try
        {
            var (state, previous) = _services.GetRequiredService<Platform.Shell.StartupRegistration>().Follow(executable, rewrite);
            if (state is not (Platform.Shell.StartupEntryState.Rewritten or Platform.Shell.StartupEntryState.OtherCopy) || previous is null)
            {
                return;
            }

            var before = Platform.Shell.ExecutableInfo.ForFile(previous);
            var exists = System.IO.File.Exists(previous);
            if (state == Platform.Shell.StartupEntryState.Rewritten)
            {
                _logger.LogInformation(
                    "Start with Windows: the sign-in entry started {Previous} at {PreviousPath} (file exists: {Exists}); it now starts this copy, {Own} at {Path}",
                    before.DisplayName, previous, exists, Platform.Shell.ExecutableInfo.Current.DisplayName, executable);
            }
            else
            {
                _logger.LogInformation(
                    "Start with Windows: the sign-in entry starts {Previous} at {PreviousPath} (file exists: {Exists}), not this Debug build, {Own} at {Path}; left as it is",
                    before.DisplayName, previous, exists, Platform.Shell.ExecutableInfo.Current.DisplayName, executable);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException
            or ArgumentException)
        {
            // An optional step: an unreadable or malformed entry must never stop Grably from starting.
            _logger.LogWarning(ex, "Could not check the start-with-Windows entry");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services.GetService<TrayIconController>()?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled UI error");
        e.Handled = true;
        ShowError($"Something went wrong: {e.Exception.Message}");
    }

    private void ShowError(string message) =>
        MessageBox.Show($"{message}\n\nDetails were written to the log in {_paths.LogsFolder}.",
            "Grably", MessageBoxButton.OK, MessageBoxImage.Error);
}
