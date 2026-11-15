using Grably.Core;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Grably.App.Hosting;

internal static class LoggingSetup
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>Daily rolling log in %LocalAppData%\Grably\logs, 14 days kept. Never logs keystrokes or capture content.</summary>
    public static Logger CreateLogger(AppPaths paths) =>
        new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(paths.LogsFolder, "grably-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true, // a second launch logs its hand-off to the same file
                outputTemplate: Template)
            .CreateLogger();
}
