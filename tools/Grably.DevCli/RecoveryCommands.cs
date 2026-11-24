using System.Diagnostics;
using Grably.Core;
using Grably.Core.Recording;
using Grably.Platform.Recording;

namespace Grably.DevCli;

/// <summary>grably-dev recovery-test.</summary>
internal static class RecoveryCommands
{
    /// <summary>How long the second grably-dev records before it is killed.</summary>
    private static readonly TimeSpan Recorded = TimeSpan.FromSeconds(4);

    /// <summary>
    /// recovery-test: starts a test-pattern recording in a second grably-dev, kills it and its ffmpeg about four
    /// seconds into the recording (as a crash or power cut would), then saves the unfinished session the way Grably's
    /// startup offer does. PASS if it is found and becomes a playable MP4 of what was recorded. Needs GRABLY_HOME set
    /// to a scratch folder, so the real Grably never finds the test's session.
    /// </summary>
    public static int RecoveryTest(string[] args)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRABLY_HOME")))
        {
            Console.WriteLine("FAIL set GRABLY_HOME to a scratch folder first: the test leaves a session for Grably to find");
            return 1;
        }

        var paths = AppPaths.FromEnvironment();
        var before = Folders(paths.SessionsFolder);
        var failures = 0;
        try
        {
            if (!RecordThenKill(out var output))
            {
                Console.WriteLine($"FAIL the recording did not start:{Environment.NewLine}{output}");
                return 1;
            }

            var found = RecordingSession.FindUnsaved(paths.SessionsFolder).Where(s => !before.Contains(s.Folder)).ToList();
            failures += Check(found.Count == 1, $"the unfinished recording is found ({found.Count})");
            if (found.Count == 1)
            {
                failures += CheckSaves(found[0].Session, found[0].Folder, paths);
            }
        }
        finally
        {
            foreach (var folder in Folders(paths.SessionsFolder).Except(before))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        Console.WriteLine(failures == 0 ? "PASS recovery-test: a recording killed mid-way is found and saved" : $"FAIL recovery-test: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Records in a second grably-dev, waits until it says recording started, lets it run, then kills it.</summary>
    private static bool RecordThenKill(out string output)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim();
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "record --pattern --seconds 60")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        child.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lines.Enqueue(line);
                if (line.StartsWith("INFO Recording ", StringComparison.Ordinal))
                {
                    started.Set();
                }
            }
        };
        child.BeginOutputReadLine();
        var ok = started.Wait(TimeSpan.FromSeconds(60));
        if (ok)
        {
            Thread.Sleep(Recorded);
        }

        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
        }

        child.WaitForExit();
        Console.WriteLine(ok ? $"INFO killed the recording after about {Recorded.TotalSeconds:0} s" : "INFO the recording never started");
        output = string.Join(Environment.NewLine, lines);
        return ok;
    }

    private static int CheckSaves(RecordingSession session, string folder, AppPaths paths)
    {
        var ffmpeg = FfmpegLocator.Find();
        Directory.CreateDirectory(paths.DefaultVideosFolder);
        var output = Path.Combine(paths.DefaultVideosFolder, $"grably-recovered-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var error = ScreenRecorder.SaveAsMp4Async(ffmpeg, session, folder, output).GetAwaiter().GetResult();
        var failures = Check(error is null, $"it saves as an MP4{(error is null ? "" : $": {error}")}");
        if (error is not null)
        {
            return failures;
        }

        // Matroska clusters close every second, so all but about the last second must be there.
        var probe = RecordingCommands.Probe(ffmpeg, output);
        failures += Check(
            probe.Codec is "h264" or "hevc" && probe.Duration >= Recorded.TotalSeconds - 1.5 && probe.Duration < Recorded.TotalSeconds + 3,
            $"which plays: {probe.Codec}, {probe.Duration:0.00} s of about {Recorded.TotalSeconds:0} s recorded, {probe.Frames} frames");
        var frame = RecordingCommands.ExtractFrame(ffmpeg, output, Math.Min(1, probe.Duration / 2));
        failures += Check((frame.GetRgb(frame.Width / 16, frame.Height / 3) & 0xF0F0F0) == 0xF0F0F0, "and shows the test pattern (the white bar)");
        File.Delete(output);
        return failures;
    }

    private static HashSet<string> Folders(string sessions) =>
        Directory.Exists(sessions) ? Directory.GetDirectories(sessions).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
