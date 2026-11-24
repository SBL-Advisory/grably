using System.Globalization;
using Grably.Core;
using Grably.Core.Recording;
using Grably.Core.Settings;
using Grably.Platform.Recording;

namespace Grably.DevCli;

/// <summary>grably-dev export.</summary>
internal static class ExportCommands
{
    /// <summary>
    /// export (video | --sample) --keep a-b,c-d [--format mp4|gif] [--height H] [--out f]: exports the kept ranges
    /// (seconds) and checks the result: its length equals the kept total within one frame; a GIF must decode.
    /// --sample makes a 10-second test video with a tone first, and for MP4 also checks each part starts on exactly the
    /// recording's frame at its cut (the frame counter in the test picture tells neighbouring frames apart).
    /// </summary>
    public static int Export(string[] args)
    {
        var ffmpeg = FfmpegLocator.Find();
        var paths = AppPaths.FromEnvironment();
        Directory.CreateDirectory(paths.DefaultVideosFolder);
        var sample = args.Contains("--sample");
        var input = sample ? MakeSample(ffmpeg, paths) : args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? throw new ArgumentException("export expects a video file or --sample.");
        var format = (OptionValue(args, "--format") ?? "mp4") == "gif" ? ExportFormat.Gif : ExportFormat.Mp4;
        var info = VideoTools.ProbeAsync(ffmpeg, input).GetAwaiter().GetResult();
        var edits = EditList.Whole(info.Duration);
        if (OptionValue(args, "--keep") is { } keep)
        {
            var ranges = keep.Split(',').Select(r => r.Split('-')).Select(p => new TimeRange(
                TimeSpan.FromSeconds(double.Parse(p[0], CultureInfo.InvariantCulture)), TimeSpan.FromSeconds(double.Parse(p[1], CultureInfo.InvariantCulture))));
            edits = new EditList(info.Duration, ranges.ToList());
        }

        var height = OptionValue(args, "--height") is { } h ? int.Parse(h, CultureInfo.InvariantCulture) : (int?)null;
        var output = Path.GetFullPath(OptionValue(args, "--out")
            ?? Path.Combine(paths.DefaultVideosFolder, $"grably-export-{DateTime.Now:yyyyMMdd-HHmmss}.{(format == ExportFormat.Gif ? "gif" : "mp4")}"));
        Console.WriteLine($"INFO {Path.GetFileName(input)}: {info.Duration.TotalSeconds:0.000} s at {info.FrameRate:0.##} fps, sound {(info.HasAudio ? "yes" : "no")}; keeping {edits.Kept.Count} part(s), {edits.Duration.TotalSeconds:0.000} s");

        var lastShown = -1;
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100) / 25 * 25;
            if (percent != lastShown)
            {
                lastShown = percent;
                Console.WriteLine($"INFO {percent}%");
            }
        });
        var exporter = new VideoExporter(ffmpeg, new EncoderProbe(ffmpeg), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var error = exporter.ExportAsync(input, edits, new ExportSettings(format, height), VideoQuality.High, output, progress).GetAwaiter().GetResult();
        if (error is not null)
        {
            Console.WriteLine($"FAIL {error}");
            return 1;
        }

        var result = VideoTools.ProbeAsync(ffmpeg, output).GetAwaiter().GetResult();
        var frame = info.FrameLength.TotalSeconds;
        var tolerance = format == ExportFormat.Gif ? 1.0 / 12 + frame : frame;
        var off = Math.Abs(result.Duration.TotalSeconds - edits.Duration.TotalSeconds);
        var failures = Check(off <= tolerance + 0.001, $"length {result.Duration.TotalSeconds:0.000} s is the kept {edits.Duration.TotalSeconds:0.000} s within one frame (off by {off * 1000:0} ms)");
        if (format == ExportFormat.Mp4)
        {
            failures += Check(result.HasAudio == info.HasAudio, $"sound {(result.HasAudio ? "kept" : "none")}");
            failures += Check(height is null || result.Height <= height, $"{result.Width}x{result.Height}");
            if (sample && height is null)
            {
                failures += CheckCutFrames(ffmpeg, input, output, edits, info.FrameRate);
            }
        }
        else
        {
            failures += Check(result.Width > 0 && output.EndsWith(".gif", StringComparison.OrdinalIgnoreCase), $"the GIF decodes ({result.Width}x{result.Height})");
        }

        Console.WriteLine(failures == 0 ? $"PASS export {output}" : $"FAIL export: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// A 10-second 1280x720 test picture with a 440 Hz tone, made with ffmpeg's own encoders. Like a recording, it goes
    /// through Matroska first, whose time stamps are whole milliseconds (frame 1 at 0.033 s, not 1/30 s).
    /// </summary>
    private static string MakeSample(string ffmpeg, AppPaths paths)
    {
        var intermediate = Path.Combine(paths.DefaultVideosFolder, "grably-sample.mkv");
        var sample = Path.ChangeExtension(intermediate, ".mp4");
        Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "10", "-c:v", "mpeg4", "-q:v", "4", "-g", "30", "-c:a", "aac", "-shortest", "-y", intermediate]);
        Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", intermediate, "-c", "copy", "-movflags", "+faststart", "-y", sample]);
        File.Delete(intermediate);
        return sample;

        static void Run(string ffmpeg, IReadOnlyList<string> args)
        {
            var (code, errors) = FfmpegProcess.RunAsync(ffmpeg, args, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
            if (code != 0)
            {
                throw new InvalidOperationException($"The sample video could not be made: {errors}");
            }
        }
    }

    /// <summary>The first frame of each part of the export must be the recording's frame at that part's start.</summary>
    private static int CheckCutFrames(string ffmpeg, string source, string export, EditList edits, double fps)
    {
        var failures = 0;
        var exported = 0;
        foreach (var part in edits.Kept)
        {
            var expected = VideoFrames.Index(part.Start, fps);
            var picture = CounterArea(ffmpeg, export, exported, fps);
            var differences = Enumerable.Range(expected - 1, 3).Where(f => f >= 0)
                .Select(f => (Frame: f, Difference: Difference(picture, CounterArea(ffmpeg, source, f, fps)))).ToList();
            var best = differences.MinBy(d => d.Difference);
            failures += Check(best.Frame == expected, $"edited frame {exported} is recording frame {expected} (closest: {best.Frame}, {string.Join(", ", differences.Select(d => $"{d.Frame}: {d.Difference:0.0}"))})");
            exported += VideoFrames.Index(part.End, fps) - expected;
        }

        return failures;
    }

    /// <summary>The top-left corner of a frame (where the test picture's time and frame number are), in grey.</summary>
    private static byte[] CounterArea(string ffmpeg, string video, int frame, double fps)
    {
        using var process = FfmpegProcess.Start(
            ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-ss", VideoFrames.SeekTime(frame, fps).TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                "-i", video, "-frames:v", "1", "-an", "-vf", "crop=256:48:0:0,format=gray", "-f", "rawvideo", "-"],
            withInput: false, withOutput: true);
        using var pixels = new MemoryStream();
        process.Output.CopyTo(pixels);
        process.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        return pixels.ToArray();
    }

    private static double Difference(byte[] a, byte[] b) =>
        a.Length == 0 || a.Length != b.Length ? double.MaxValue : a.Zip(b, (x, y) => Math.Abs(x - y)).Average();

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        return ok ? 0 : 1;
    }

    private static string? OptionValue(string[] args, string name) =>
        Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
