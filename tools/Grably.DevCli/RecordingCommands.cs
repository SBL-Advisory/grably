using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Grably.Core;
using Grably.Core.Geometry;
using Grably.Core.Imaging;
using Grably.Core.Recording;
using Grably.Platform.Capture;
using Grably.Platform.Display;
using Grably.Platform.Recording;
using Microsoft.Extensions.Logging;

namespace Grably.DevCli;

/// <summary>grably-dev encoders, record and bench.</summary>
internal static class RecordingCommands
{
    /// <summary>encoders [--size WxH]: probes every candidate encoder with a real encode at that size.</summary>
    public static int Encoders(string[] args)
    {
        var (width, height) = ParseSize(OptionValue(args, "--size") ?? "1920x1080");
        var ffmpeg = FfmpegLocator.Find();
        Console.WriteLine($"INFO ffmpeg {ffmpeg}");
        var results = new EncoderProbe(ffmpeg).TestAllAsync(width, height).GetAwaiter().GetResult();
        foreach (var (encoder, works, error) in results)
        {
            Console.WriteLine($"{(works ? "works " : "fails ")} {encoder.Name,-12} {(works ? "" : error)}");
        }

        var chosen = results.FirstOrDefault(r => r.Works).Encoder;
        Console.WriteLine(chosen is null ? $"FAIL no encoder works at {width}x{height}" : $"PASS {chosen.Name} would record {width}x{height}");
        return chosen is null ? 1 : 0;
    }

    /// <summary>
    /// record (--monitor N | --window title | --region x,y,w,h | --pattern) [--seconds S] [--fps F] [--bgra] [--out f.mp4]:
    /// records, saves an MP4 and checks it with ffprobe (codec, frame rate, duration) and an extracted frame.
    /// </summary>
    public static int Record(string[] args)
    {
        var seconds = double.Parse(OptionValue(args, "--seconds") ?? "5", CultureInfo.InvariantCulture);
        var fps = int.Parse(OptionValue(args, "--fps") ?? "30", CultureInfo.InvariantCulture);
        var paths = AppPaths.FromEnvironment();
        var output = Path.GetFullPath(OptionValue(args, "--out") ?? Path.Combine(paths.DefaultVideosFolder, $"grably-dev-{DateTime.Now:yyyyMMdd-HHmmss}.mp4"));
        var pattern = args.Contains("--pattern");
        var (result, ffmpeg) = RecordFor(args, seconds, fps, paths);
        if (!result.Succeeded)
        {
            Console.WriteLine($"FAIL recording: {result.Error}");
            return 1;
        }

        Report(result);
        if (ScreenRecorder.SaveAsMp4Async(ffmpeg, result.VideoFile, output).GetAwaiter().GetResult() is { } saveError)
        {
            Console.WriteLine($"FAIL {saveError}");
            return 1;
        }

        var failures = 0;
        var probe = Probe(ffmpeg, output);
        Console.WriteLine($"INFO {output}: {probe.Codec} {probe.Width}x{probe.Height} at {probe.FrameRate} fps, {probe.Duration:0.000} s, {probe.Frames} frames, {probe.PixelFormat} {probe.ColorSpace}");
        failures += Check(probe.Codec is "h264" or "hevc", $"codec is {probe.Codec}");
        failures += Check(Math.Abs(probe.FrameRate - fps) < 0.01, $"frame rate is {probe.FrameRate} (asked {fps})");
        failures += Check(Math.Abs(probe.Duration - seconds) <= 0.2, $"duration {probe.Duration:0.000} s is within 0.2 s of {seconds} s");
        failures += Check(probe.PixelFormat == "yuv420p" || probe.PixelFormat == "nv12", $"pixel format {probe.PixelFormat} plays everywhere");

        var frame = ExtractFrame(ffmpeg, output, Math.Min(1, seconds / 2));
        if (pattern)
        {
            failures += CheckBars(frame);
        }
        else
        {
            // Screen content is not looked at (it is whatever the user has open); only that there is some.
            var colours = new HashSet<int>();
            for (var y = 0; y < frame.Height; y += 7)
            {
                for (var x = 0; x < frame.Width; x += 7)
                {
                    colours.Add(frame.GetRgb(x, y));
                }
            }

            failures += Check(colours.Count > 16, $"the frame shows something ({colours.Count} colours sampled)");
            if (!args.Contains("--window"))
            {
                failures += CheckNoBorder(frame);
            }
        }

        Console.WriteLine(failures == 0 ? $"PASS record {output}" : $"FAIL record: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>bench [--monitor N | --pattern] [--seconds S] [--fps F] [--bgra]: can the engine keep up at this size?</summary>
    public static int Bench(string[] args)
    {
        var seconds = double.Parse(OptionValue(args, "--seconds") ?? "10", CultureInfo.InvariantCulture);
        var fps = int.Parse(OptionValue(args, "--fps") ?? "30", CultureInfo.InvariantCulture);
        var paths = AppPaths.FromEnvironment();
        if (!args.Contains("--pattern") && !args.Contains("--window") && !args.Contains("--region") && !args.Contains("--monitor"))
        {
            args = [.. args, "--monitor", "0"];
        }

        var (result, _) = RecordFor(args, seconds, fps, paths);
        if (!result.Succeeded)
        {
            Console.WriteLine($"FAIL recording: {result.Error}");
            return 1;
        }

        Report(result);
        var composedFps = result.Composed / result.Duration.TotalSeconds;
        Console.WriteLine($"INFO made {composedFps:0.0} frames per second, {result.AverageComposeMilliseconds:0.0} ms each");
        // A few frames may be repeated while the encoder starts; after that, none may be lost.
        var droppedLater = result.Dropped - result.DroppedAtStart;
        var ok = droppedLater == 0 && result.Repeats <= Math.Max(2, result.Frames / 100);
        Console.WriteLine(ok
            ? $"PASS bench {result.Session.Width}x{result.Session.Height} at {fps} fps: no frames dropped after start-up ({result.DroppedAtStart} while starting), {result.Repeats} repeated"
            : $"FAIL bench: {droppedLater} dropped for the encoder after start-up, {result.Repeats} repeated late of {result.Frames}");
        try
        {
            Directory.Delete(result.SessionFolder, recursive: true); // the recording itself is not needed
        }
        catch (IOException)
        {
        }

        return ok ? 0 : 1;
    }

    private static (RecordingResult Result, string Ffmpeg) RecordFor(string[] args, double seconds, int fps, AppPaths paths)
    {
        var ffmpeg = FfmpegLocator.Find();
        var options = new RecorderOptions
        {
            Target = Target(args),
            FrameRate = fps,
            PreferNv12 = !args.Contains("--bgra"),
            Ffmpeg = ffmpeg,
            SessionsFolder = paths.SessionsFolder,
        };
        var logger = new ConsoleLogger();
        var recorder = ScreenRecorder.StartAsync(options, new EncoderProbe(ffmpeg, logger), logger).GetAwaiter().GetResult();
        var stop = new ManualResetEventSlim();
        recorder.SourceClosed += (_, _) => stop.Set();
        stop.Wait(TimeSpan.FromSeconds(seconds) - recorder.Elapsed);
        return (recorder.StopAsync().GetAwaiter().GetResult(), ffmpeg);
    }

    private static CaptureTarget? Target(string[] args)
    {
        if (args.Contains("--pattern"))
        {
            return null;
        }

        var monitors = MonitorService.GetMonitors();
        if (OptionValue(args, "--window") is { } title)
        {
            var window = WindowEnumerator.GetVisibleWindows().FirstOrDefault(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No visible window has '{title}' in its title.");
            return CaptureTarget.ForWindow(window);
        }

        if (OptionValue(args, "--region") is { } region)
        {
            var parts = region.Split(',').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            return CaptureTarget.ForRegion(new PixelRect(parts[0], parts[1], parts[2], parts[3]), monitors);
        }

        var index = int.Parse(OptionValue(args, "--monitor") ?? "0", CultureInfo.InvariantCulture);
        return CaptureTarget.ForMonitor(monitors[index]);
    }

    private static void Report(RecordingResult result) =>
        Console.WriteLine(
            $"INFO {result.Session.Target}: {result.Session.Width}x{result.Session.Height} {result.Format} with {result.Session.Encoder}, "
            + $"{result.Frames} frames ({result.Composed} made, {result.Repeats} repeated late, {result.Dropped} dropped for the encoder, {result.DroppedAtStart} of them while starting)");

    private sealed record ProbeResult(string Codec, int Width, int Height, double FrameRate, double Duration, long Frames, string PixelFormat, string ColorSpace);

    private static ProbeResult Probe(string ffmpeg, string video)
    {
        var info = new ProcessStartInfo(FfmpegLocator.ProbeNextTo(ffmpeg)) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries",
                     "stream=codec_name,width,height,avg_frame_rate,nb_read_packets,pix_fmt,color_space:format=duration", "-of", "json", video })
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var json = JsonDocument.Parse(process.StandardOutput.ReadToEnd()).RootElement;
        process.WaitForExit();
        var stream = json.GetProperty("streams")[0];
        var rate = stream.GetProperty("avg_frame_rate").GetString()!.Split('/');
        return new ProbeResult(
            stream.GetProperty("codec_name").GetString()!,
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32(),
            double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture),
            double.Parse(json.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture),
            long.Parse(stream.GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture),
            stream.GetProperty("pix_fmt").GetString()!,
            stream.TryGetProperty("color_space", out var space) ? space.GetString()! : "?");
    }

    private static PixelBuffer ExtractFrame(string ffmpeg, string video, double seconds)
    {
        var png = Path.Combine(Path.GetTempPath(), $"grably-frame-{Guid.NewGuid():N}.png");
        var (code, errors) = FfmpegProcess.RunAsync(ffmpeg, FfmpegArguments.ExtractFrame(video, seconds, png), TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        if (code != 0)
        {
            throw new InvalidOperationException($"Could not extract a frame: {errors}");
        }

        try
        {
            using var stream = File.OpenRead(png);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            return Grably.App.Imaging.BitmapConversion.ToPixelBuffer(frame);
        }
        finally
        {
            File.Delete(png);
        }
    }

    /// <summary>The middle of each colour bar must come back within a small tolerance (catches range and matrix mistakes).</summary>
    private static int CheckBars(PixelBuffer frame)
    {
        var failures = 0;
        var barWidth = frame.Width / TestPattern.Bars.Count;
        for (var i = 0; i < TestPattern.Bars.Count; i++)
        {
            var expected = TestPattern.Bars[i];
            var actual = frame.GetRgb((i * barWidth) + (barWidth / 2), frame.Height / 3);
            var worst = new[] { 16, 8, 0 }.Max(shift => Math.Abs(((expected >> shift) & 0xFF) - ((actual >> shift) & 0xFF)));
            failures += Check(worst <= 12, $"bar {i + 1} is #{actual:X6}, expected #{expected:X6} (off by {worst})");
        }

        return failures;
    }

    /// <summary>Windows draws a yellow border around a captured area unless borderless capture is allowed.</summary>
    private static int CheckNoBorder(PixelBuffer frame)
    {
        var edge = Enumerable.Range(0, frame.Width).Select(x => frame.GetRgb(x, 0))
            .Concat(Enumerable.Range(0, frame.Height).Select(y => frame.GetRgb(0, y))).ToList();
        var yellow = edge.Count(rgb => ((rgb >> 16) & 0xFF) > 200 && ((rgb >> 8) & 0xFF) > 150 && (rgb & 0xFF) < 90);
        return Check(yellow < edge.Count / 2, $"no capture border ({yellow} of {edge.Count} edge pixels are border yellow)");
    }

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        return ok ? 0 : 1;
    }

    private static (int, int) ParseSize(string text)
    {
        var parts = text.Split('x');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static string? OptionValue(string[] args, string name) =>
        Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

    /// <summary>Prints the recorder's information and warnings as INFO and WARN lines.</summary>
    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Console.WriteLine($"{(logLevel >= LogLevel.Warning ? "WARN" : "INFO")} {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
            }
        }
    }
}
