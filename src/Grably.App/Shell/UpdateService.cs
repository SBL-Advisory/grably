using System.Windows;
using Grably.App.Recording;
using Grably.Core;
using Grably.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Grably.App.Shell;

/// <summary>
/// Keeps an installed Grably up to date from GitHub Releases with Velopack.
/// <list type="bullet">
/// <item>Checks run quietly 30 seconds after startup and then every 12 hours while the setting is on (30 minutes
/// after a failed check), and whenever the user asks.</item>
/// <item>A newer version is downloaded in the background. It is installed when the user chooses Restart to update,
/// or by <see cref="InstallPendingAtStartup"/> the next time Grably starts.</item>
/// </list>
/// Update problems are logged and never get in the user's way.
/// </summary>
/// <param name="services">For the recorder, looked up when needed (it depends on the tray, which depends on this).</param>
public sealed class UpdateService(
    IServiceProvider services, SettingsStore settings, UserNotifications notifications, ILogger<UpdateService> logger)
{
    public const string Repository = "https://github.com/SBL-Advisory/grably";

    /// <summary>A folder or web address to update from instead of GitHub Releases (for testing an update).</summary>
    public const string FeedVariable = "GRABLY_UPDATE_FEED";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _checking = new(1, 1);
    private UpdateManager? _manager;
    private VelopackAsset? _ready;

    /// <summary>An update was downloaded and waits for a restart. Raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The user chose to restart into the update; <see cref="AppCommands"/> runs the exit checks.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>The version waiting for a restart, if any.</summary>
    public string? ReadyVersion => _ready?.Version.ToString();

    private UpdateManager Manager => _manager ??= CreateManager();

    private static UpdateManager CreateManager() => Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } feed
        ? new UpdateManager(feed)
        : new UpdateManager(new GithubSource(Repository, accessToken: null, prerelease: false));

    /// <summary>
    /// At startup, in the one Grably that runs (never in a second launch that only hands over to it, which would swap
    /// the files under the running one): installs an update downloaded earlier and starts it. Exits the process when it
    /// does; returns false when there is nothing to install or it cannot be done.
    /// </summary>
    public static bool InstallPendingAtStartup(string[] args, Serilog.ILogger log)
    {
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } pending)
            {
                return false;
            }

            log.Information("Installing update {Version}, downloaded earlier", pending.Version);
            manager.ApplyUpdatesAndRestart(pending, args); // exits this process
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Warning(ex, "Could not install the downloaded update; starting the current version");
            return false;
        }
    }

    /// <summary>The automatic checks, for as long as Grably runs.</summary>
    public async Task CheckRegularlyAsync()
    {
        var wait = StartupDelay;
        while (_ready is null)
        {
            await Task.Delay(wait);
            wait = !settings.Current.General.CheckForUpdates || await CheckAsync(userAsked: false) ? CheckInterval : RetryInterval;
        }
    }

    /// <summary>
    /// Looks for a newer version and downloads it. <paramref name="userAsked"/>: say what happened (up to date,
    /// failed) and offer to restart; otherwise only a ready update is mentioned. False when the check failed.
    /// </summary>
    public async Task<bool> CheckAsync(bool userAsked)
    {
        if (_ready is null)
        {
            if (!await _checking.WaitAsync(0))
            {
                if (userAsked)
                {
                    notifications.Show("Already checking for updates.");
                }

                return true;
            }

            try
            {
                if (!await DownloadAsync(userAsked))
                {
                    return false;
                }
            }
            finally
            {
                _checking.Release();
            }
        }

        if (_ready is not null && userAsked)
        {
            OfferRestart();
        }

        return true;
    }

    private async Task<bool> DownloadAsync(bool userAsked)
    {
        try
        {
            if (!Manager.IsInstalled)
            {
                logger.LogInformation("Not checking for updates: this copy of Grably was not installed");
                if (userAsked)
                {
                    notifications.Show("Updates work in installed copies of Grably; this one runs from a build folder.");
                }

                return true;
            }

            logger.LogInformation("Checking for updates");
            var update = await Manager.CheckForUpdatesAsync();
            if (update is null)
            {
                logger.LogInformation("Grably is up to date");
                if (userAsked)
                {
                    notifications.Show($"Grably {AppInfo.Version} is up to date.");
                }

                return true;
            }

            var version = update.TargetFullRelease.Version;
            logger.LogInformation("Downloading update {Version}", version);
            if (userAsked)
            {
                notifications.Show($"Downloading Grably {version}...");
            }

            await Manager.DownloadUpdatesAsync(update);
            _ready = update.TargetFullRelease;
            logger.LogInformation("Update {Version} downloaded", version);
            Changed?.Invoke(this, EventArgs.Empty);
            if (!userAsked && services.GetRequiredService<RecordingController>().State == RecordingState.Idle)
            {
                // Never in the middle of a recording; the tray menu says it too.
                notifications.Show($"Grably {version} is ready. Choose Restart to update in the tray menu, or it installs the next time Grably starts.");
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The update check failed");
            if (userAsked)
            {
                notifications.Show($"Could not check for updates: {ex.Message}");
            }

            return false;
        }
    }

    /// <summary>Whether a downloaded update can be installed now (checked before asking the user to quit).</summary>
    public bool CanInstall => _ready is not null && Manager.IsInstalled;

    /// <summary>
    /// Hands the downloaded update to Velopack's updater, which waits for Grably to exit, installs it and starts the
    /// new version in the background. The caller then exits normally. False when that could not be started.
    /// </summary>
    public bool InstallOnExit()
    {
        if (_ready is null)
        {
            return false;
        }

        try
        {
            logger.LogInformation("Restarting to install update {Version}", _ready.Version);
            Manager.WaitExitThenApplyUpdates(_ready, silent: false, restart: true, restartArgs: ["--background"]);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Could not start installing the update");
            return false;
        }
    }

    private void OfferRestart()
    {
        if (MessageBox.Show(
                $"Grably {ReadyVersion} is ready. Restart Grably now to update?", "Grably", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes)
        {
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
