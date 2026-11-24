using System.Diagnostics;
using System.Windows;
using Grably.Core;
using Grably.Platform.Shell;

namespace Grably.App.Shell;

/// <summary>
/// A launch while another Grably already runs for this user. The same Grably.exe, and any <c>--background</c> launch
/// (at sign-in), hand over to it as always. A different Grably.exe (another build folder, a portable or moved copy)
/// asks first, so that nobody ends up in an older copy without knowing: close the running one and start this one
/// instead, or keep using the running one.
/// </summary>
/// <remarks>
/// Runs before the WPF application exists, so its dialogs are plain message boxes. Every decision is logged with both
/// copies' paths and versions.
/// </remarks>
public static class RunningCopy
{
    /// <summary>How long the running Grably's launch pipe may take to answer (as before).</summary>
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A Grably that can be closed from outside answers on its close pipe at once.</summary>
    private static readonly TimeSpan CloseChannelTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The running Grably asks its own questions first and may stop and save a recording.</summary>
    private static readonly TimeSpan CloseReplyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>After agreeing (or when it went away mid-request), it stops its services (up to 5 s) and exits.</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>No answer in time: its question is still open, so only a short grace period.</summary>
    private static readonly TimeSpan NoReplyExitTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The single-instance lock can stay taken for a moment after the old process exits.</summary>
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Hands this launch to the running Grably, or closes that one and returns the single-instance guard to start
    /// with. Null means this launch is done; <paramref name="exitCode"/> is then its exit code.
    /// </summary>
    public static SingleInstanceGuard? HandOverOrReplace(
        IReadOnlyList<string> args, LaunchOptions options, Serilog.ILogger log, out int exitCode)
    {
        var own = ExecutableInfo.Current;
        if (SingleInstanceGuard.FindPrimary(AppInfo.Id, LaunchTimeout) is not { } processId)
        {
            log.Information("{App} holds the single-instance lock but did not answer; this launch is {Own} from {Path}", AppInfo.Name, own.DisplayName, own.Path);
            return StartIfGone(log, out exitCode);
        }

        var running = ExecutableInfo.ForProcess(processId);
        var action = SecondLaunch.Decide(own.Path, running?.Path, options.Background);
        log.Information(
            "{Running} is already running from {RunningPath} (process {ProcessId}); this launch is {Own} from {Path} (background: {Background}): {Action}",
            running?.DisplayName ?? AppInfo.Name, running?.Path, processId, own.DisplayName, own.Path, options.Background, action.ToString());
        if (action == SecondLaunchAction.HandOver || running is null)
        {
            return HandOver(args, log, out exitCode);
        }

        return Replace(args, own, running, processId, log, out exitCode);
    }

    private static SingleInstanceGuard? Replace(
        IReadOnlyList<string> args, ExecutableInfo own, ExecutableInfo running, int processId, Serilog.ILogger log, out int exitCode)
    {
        using var process = OpenProcess(processId);
        CloseRequestResult result;
        using (var channel = SingleInstanceGuard.ConnectForClose(AppInfo.Id, processId, CloseChannelTimeout))
        {
            if (channel is null)
            {
                if (SecondLaunch.AcceptsCloseRequests(running.Version))
                {
                    // It has a close pipe but did not answer: another launch is asking it at this moment.
                    log.Warning("{Running} at {RunningPath} did not answer on its close pipe; busy with another launch", running.DisplayName, running.Path);
                    Tell($"{running.DisplayName} is busy answering another launch of Grably, so {own.DisplayName} did not start. Try again in a moment.");
                    exitCode = 1;
                    return null;
                }

                var open = Ask(OlderCopyText(running, own), MessageBoxImage.Information);
                log.Information(
                    "{Running} at {RunningPath} cannot be closed from here (1.0.1 or earlier); the user chose to {Choice}",
                    running.DisplayName, running.Path, open ? "open it" : "quit it from its tray icon");
                if (open)
                {
                    return HandOver(args, log, out exitCode);
                }

                exitCode = 0;
                return null;
            }

            if (!Ask(ReplaceText(running, own), MessageBoxImage.Question))
            {
                log.Information("The user chose to keep {Running} at {RunningPath}", running.DisplayName, running.Path);
                channel.Dispose();
                return HandOver(args, log, out exitCode);
            }

            log.Information(
                "The user chose to close {Running} at {RunningPath} and start {Own} from {Path}",
                running.DisplayName, running.Path, own.DisplayName, own.Path);
            result = channel.RequestCloseAsync(new CloseRequest(own.Path, own.Version, Environment.ProcessId), CloseReplyTimeout)
                .GetAwaiter().GetResult();
        }

        log.Information("{Running} at {RunningPath} answered: {Result}", running.DisplayName, running.Path, result.ToString());
        if (result == CloseRequestResult.Declined)
        {
            Tell($"{running.DisplayName} stayed open, so {own.DisplayName} did not start.");
            exitCode = 0;
            return null;
        }

        var exitWait = result == CloseRequestResult.NoReply ? NoReplyExitTimeout : ExitTimeout;
        if (process is not null && !process.WaitForExit(exitWait))
        {
            log.Warning("{Running} at {RunningPath} did not exit within {Seconds} s", running.DisplayName, running.Path, exitWait.TotalSeconds);
            Tell($"{running.DisplayName} did not close in time, so {own.DisplayName} did not start. Once it has closed, start Grably again.");
            exitCode = 1;
            return null;
        }

        if (AcquireAfterExit() is not { } guard)
        {
            log.Warning("{Running} exited, but the single-instance lock is taken by another launch", running.DisplayName);
            Tell($"Another Grably started in the meantime, so {own.DisplayName} did not start.");
            exitCode = 1;
            return null;
        }

        log.Information(
            "{Running} at {RunningPath} has closed; starting {Own} from {Path} in its place",
            running.DisplayName, running.Path, own.DisplayName, own.Path);
        exitCode = 0;
        return guard;
    }

    /// <summary>Hands the launch over; if the running Grably has quit meanwhile, this one starts instead.</summary>
    private static SingleInstanceGuard? HandOver(IReadOnlyList<string> args, Serilog.ILogger log, out int exitCode)
    {
        exitCode = 0;
        return SingleInstanceGuard.SendToPrimary(AppInfo.Id, Program.ForwardedArguments(args), LaunchTimeout)
            ? null
            : StartIfGone(log, out exitCode);
    }

    /// <summary>
    /// The running Grably did not answer. If it has quit in the meantime this launch starts; otherwise (it runs as
    /// administrator, or hangs) the user is told.
    /// </summary>
    private static SingleInstanceGuard? StartIfGone(Serilog.ILogger log, out int exitCode)
    {
        exitCode = 0;
        if (SingleInstanceGuard.TryAcquire(AppInfo.Id) is { } guard)
        {
            log.Information("The running {App} has quit in the meantime; starting this one", AppInfo.Name);
            return guard;
        }

        log.Warning("The running {App} did not answer", AppInfo.Name);
        Tell("Grably is already running but did not respond. If it was started as administrator, open it from its tray "
            + "icon or close it before starting Grably again.");
        exitCode = 1;
        return null;
    }

    private static SingleInstanceGuard? AcquireAfterExit()
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (SingleInstanceGuard.TryAcquire(AppInfo.Id) is { } guard)
            {
                return guard;
            }

            if (clock.Elapsed > AcquireTimeout)
            {
                return null;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>The running Grably, to wait for it to exit; null if it has already gone.</summary>
    private static Process? OpenProcess(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string ReplaceText(ExecutableInfo running, ExecutableInfo own) =>
        $"{running.DisplayName} is already running from:\n{running.Folder}\n\n"
        + $"You started {own.DisplayName} from:\n{own.Folder}\n\n"
        + "Close the running Grably and start this one instead? If it is recording, exporting or has unsaved "
        + "changes, it asks you first.\n\n"
        + "Yes: close it and start this one.\n"
        + "No: keep using the running Grably.";

    public static string OlderCopyText(ExecutableInfo running, ExecutableInfo own) =>
        $"{running.DisplayName} is already running from:\n{running.Folder}\n\n"
        + "It cannot be closed from here (Grably 1.0.1 and earlier do not allow it). "
        + $"To use {own.DisplayName} from:\n{own.Folder}\n"
        + "quit the running Grably from its tray icon (right-click it, then Exit) and start Grably again.\n\n"
        + "Open the running Grably now?";

    private static bool Ask(string text, MessageBoxImage image) =>
        MessageBox.Show(text, AppInfo.Name, MessageBoxButton.YesNo, image, MessageBoxResult.Yes) == MessageBoxResult.Yes;

    private static void Tell(string text) =>
        MessageBox.Show(text, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
}
