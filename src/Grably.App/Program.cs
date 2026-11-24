using Grably.App.Hosting;
using Grably.Core;
using Grably.Platform.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Velopack;

namespace Grably.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // First of all: when Velopack's installer, updater or uninstaller starts Grably for one of its hooks, it handles
        // that and exits here. A downloaded update is installed further down, only once this is the one running Grably.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => RemoveStartup())
            .Run();

        var options = LaunchOptions.Parse(args);
        var paths = AppPaths.FromEnvironment();
        paths.EnsureCreated();
        Log.Logger = LoggingSetup.CreateLogger(paths);
        var log = Log.ForContext(typeof(Program));
        try
        {
            log.Information("{App} {Version} starting", AppInfo.Name, AppInfo.Version);
            if (options.Snapshot is not null)
            {
                return Run(options, paths, guard: null);
            }

            // Another Grably may be running: hand over to it, or (a different Grably.exe, if the user agrees) close it
            // and start this one instead.
            var exitCode = 0;
            using var guard = SingleInstanceGuard.TryAcquire(AppInfo.Id)
                ?? Shell.RunningCopy.HandOverOrReplace(args, options, log, out exitCode);
            if (guard is null)
            {
                return exitCode;
            }

            if (Shell.UpdateService.InstallPendingAtStartup(args, log))
            {
                return 0;
            }

            return Run(options, paths, guard);
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "{App} stopped because of an unhandled error", AppInfo.Name);
            return 1;
        }
        finally
        {
            log.Information("{App} exiting", AppInfo.Name);
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Uninstalling: Windows must not try to start a Grably that is gone at the next sign-in. An entry for another copy
    /// (a portable one the user now runs) is left alone.
    /// </summary>
    private static void RemoveStartup()
    {
        try
        {
            new StartupRegistration().RemoveFor(ExecutableInfo.Current.Path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException)
        {
            // Uninstalling goes on; a Run entry pointing at a missing file does no harm beyond a failed start.
        }
    }

    /// <summary>
    /// The arguments to hand to the running instance, with file paths made absolute: the running instance has its
    /// own working directory, so "Grably.exe shot.png" from a terminal would otherwise open the wrong file.
    /// </summary>
    public static string[] ForwardedArguments(IEnumerable<string> args) =>
        args.Select(a => !a.StartsWith("--", StringComparison.Ordinal) && System.IO.File.Exists(a) ? System.IO.Path.GetFullPath(a) : a)
            .ToArray();

    private static int Run(LaunchOptions options, AppPaths paths, SingleInstanceGuard? guard)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(paths);
        builder.Services.AddGrablyApp();

        using var host = builder.Build();
        host.Start();

        var app = host.Services.GetRequiredService<App>();
        app.InitializeComponent();
        var guardLogger = host.Services.GetRequiredService<ILogger<SingleInstanceGuard>>();
        guard?.Listen(app.OnLaunchedAgain, guardLogger);
        guard?.ListenForCloseRequests(app.OnCloseRequested, app.CloseForAnotherCopy, guardLogger);

        var exitCode = app.Run();

        // Stop accepting launches first, so one arriving during shutdown is not reported as handled.
        guard?.StopListening();
        host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return exitCode;
    }
}
