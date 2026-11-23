using Grably.App.Hosting;
using Grably.Core;
using Grably.Platform.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Grably.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // M10: VelopackApp.Build().Run() must be the very first call here.
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

            using var guard = SingleInstanceGuard.TryAcquire(AppInfo.Id);
            if (guard is null)
            {
                log.Information("{App} is already running; forwarding this launch to it", AppInfo.Name);
                if (SingleInstanceGuard.SendToPrimary(AppInfo.Id, ForwardedArguments(args), TimeSpan.FromSeconds(3)))
                {
                    return 0;
                }

                log.Warning("The running {App} did not answer", AppInfo.Name);
                System.Windows.MessageBox.Show(
                    "Grably is already running but did not respond. If it was started as administrator, open it from "
                    + "its tray icon or close it before starting Grably again.",
                    "Grably", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return 1;
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
        guard?.Listen(app.OnLaunchedAgain, host.Services.GetRequiredService<ILogger<SingleInstanceGuard>>());

        var exitCode = app.Run();

        // Stop accepting launches first, so one arriving during shutdown is not reported as handled.
        guard?.StopListening();
        host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return exitCode;
    }
}
