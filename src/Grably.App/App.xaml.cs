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
        if (LaunchOptions.Parse(args).Background)
        {
            return; // e.g. a second start-with-Windows launch: nothing to show
        }

        Dispatcher.BeginInvoke(() => _services.GetRequiredService<AppCommands>().OpenLibrary());
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

            _services.GetRequiredService<TrayIconController>().Show();
            _services.GetRequiredService<HotkeyController>().Start();
            _logger.LogInformation("Ready");

            if (!_options.Background)
            {
                _services.GetRequiredService<AppCommands>().OpenLibrary();
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
