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
        if (ScreenRecorder.SaveAsMp4Async(ffmpeg, result.Session, result.SessionFolder, output).GetAwaiter().GetResult() is { } saveError)
        {
            Console.WriteLine($"FAIL {saveError}");
            return 1;
        }

        Directory.Delete(result.SessionFolder, recursive: true); // saved: nothing for the app to offer back
        var failures = 0;
        var probe = Probe(ffmpeg, output);
        Console.WriteLine($"INFO {output}: {probe.Codec} {probe.Width}x{probe.Height} at {probe.FrameRate} fps, {probe.Duration:0.000} s, {probe.Frames} frames, {probe.PixelFormat} {probe.ColorSpace}");
        failures += Check(probe.Codec is "h264" or "hevc", $"codec is {probe.Codec}");
        failures += Check(Math.Abs(probe.FrameRate - fps) < 0.01, $"frame rate is {probe.FrameRate} (asked {fps})");
        failures += Check(Math.Abs(probe.Duration - seconds) <= 0.2, $"duration {probe.Duration:0.000} s is within 0.2 s of {seconds} s");
        failures += Check(probe.PixelFormat == "yuv420p" || probe.PixelFormat == "nv12", $"pixel format {probe.PixelFormat} plays everywhere");
        if (args.Contains("--system-audio") || args.Contains("--mic"))
        {
            failures += Check(probe.AudioCodec == "aac", $"there is a sound track ({probe.AudioCodec ?? "none"})");
        }

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

    /// <summary>
    /// synctest [--seconds S] [--pause]: records the test pattern with a white flash and a beep at the same recording
    /// times (1 s, the middle, S - 1 s), saves the MP4 and measures with ffmpeg where each flash and each beep start.
    /// With --pause the recording is paused for two seconds in the middle, which must not shift anything.
    /// </summary>
    public static int SyncTest(string[] args)
    {
        var seconds = double.Parse(OptionValue(args, "--seconds") ?? "10", CultureInfo.InvariantCulture);
        var paths = AppPaths.FromEnvironment();
        TimeSpan[] signals = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(seconds / 2 + 0.5), TimeSpan.FromSeconds(seconds - 1)];
        var pauseAt = args.Contains("--pause") ? TimeSpan.FromSeconds(seconds / 2) : (TimeSpan?)null;
        var (result, ffmpeg) = RecordFor(["--pattern"], seconds, 30, paths, signals, pauseAt);
        if (!result.Succeeded)
        {
            Console.WriteLine($"FAIL recording: {result.Error}");
            return 1;
        }

        var output = Path.Combine(paths.DefaultVideosFolder, $"grably-synctest-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        if (ScreenRecorder.SaveAsMp4Async(ffmpeg, result.Session, result.SessionFolder, output).GetAwaiter().GetResult() is { } saveError)
        {
            Console.WriteLine($"FAIL {saveError}");
            return 1;
        }

        Directory.Delete(result.SessionFolder, recursive: true);

        var flashes = Onsets(RunForLog(ffmpeg, ["-i", output, "-vf", "signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG", "-an", "-f", "null", "-"]));
        var probe = Probe(ffmpeg, output);

        // silencedetect also reports the end of the file as the end of a silence; that is not a beep.
        var beeps = BeepOnsets(RunForLog(ffmpeg, ["-i", output, "-af", "silencedetect=n=-30dB:d=0.05", "-vn", "-f", "null", "-"]))
            .Where(b => b < probe.Duration - 0.1).ToList();
        Console.WriteLine($"INFO flashes at {string.Join(", ", flashes.Select(f => $"{f:0.000}"))} s; beeps at {string.Join(", ", beeps.Select(b => $"{b:0.000}"))} s");
        var failures = Check(Math.Abs(probe.Duration - seconds) <= 0.2, $"duration {probe.Duration:0.000} s is within 0.2 s of {seconds} s{(pauseAt is null ? "" : " (the pause is not in it)")}");
        failures += Check(flashes.Count == signals.Length && beeps.Count == signals.Length, $"{signals.Length} flashes and {signals.Length} beeps found ({flashes.Count}, {beeps.Count})");
        for (var i = 0; i < Math.Min(flashes.Count, beeps.Count); i++)
        {
            var offset = (beeps[i] - flashes[i]) * 1000;
            failures += Check(Math.Abs(offset) <= 80, $"signal {i + 1}: audio {offset:+0;-0} ms from video (limit 80)");
            failures += Check(Math.Abs(flashes[i] - signals[i].TotalSeconds) <= 0.05, $"signal {i + 1}: flash at {flashes[i]:0.000} s, sent at {signals[i].TotalSeconds:0.000} s");
        }

        Console.WriteLine(failures == 0 ? $"PASS synctest {output}" : $"FAIL synctest: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Times where the picture turns white: frame times whose average brightness jumps above 200.</summary>
    private static List<double> Onsets(string log)
    {
        var onsets = new List<double>();
        double? time = null;
        var wasBright = false;
        foreach (var line in log.Split('\n'))
        {
            if (System.Text.RegularExpressions.Regex.Match(line, @"pts_time:([0-9.]+)") is { Success: true } t)
            {
                time = double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if (System.Text.RegularExpressions.Regex.Match(line, @"YAVG=([0-9.]+)") is { Success: true } y && time is { } at)
            {
                var bright = double.Parse(y.Groups[1].Value, CultureInfo.InvariantCulture) > 200;
                if (bright && !wasBright)
                {
                    onsets.Add(at);
                }

                wasBright = bright;
            }
        }

        return onsets;
    }

    /// <summary>Times where silence ends: the beeps.</summary>
    private static List<double> BeepOnsets(string log) =>
        System.Text.RegularExpressions.Regex.Matches(log, @"silence_end: ([0-9.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();

    /// <summary>Runs ffmpeg at info level and returns everything it logged.</summary>
    private static string RunForLog(string ffmpeg, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-nostats", "-loglevel", "info" }.Concat(arguments))
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var log = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return log;
    }

    /// <summary>
    /// exclusion-test: shows two magenta squares (a layered WPF window like the control bar, and an opaque one),
    /// excluded from capture, then checks they are absent from a Windows.Graphics.Capture recording and from a GDI
    /// screenshot of the same area.
    /// </summary>
    public static int ExclusionTest(string[] args)
    {
        var monitor = MonitorService.GetMonitors().First(m => m.IsPrimary);
        var area = new PixelRect(monitor.Bounds.X + 100, monitor.Bounds.Y + 100, 640, 320);
        var layered = new PixelRect(area.X + 40, area.Y + 60, 200, 200);
        var opaque = new PixelRect(area.X + 380, area.Y + 60, 200, 200);
        using var shown = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            System.Windows.Window Square(PixelRect at, bool transparent)
            {
                var window = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None,
                    AllowsTransparency = transparent,
                    Background = System.Windows.Media.Brushes.Magenta,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ShowActivated = false,
                    Width = 10,
                    Height = 10,
                    Title = "Grably exclusion test",
                };
                window.SourceInitialized += (_, _) =>
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    WindowActions.ExcludeFromCapture(handle);
                    ScreenPointer.PlaceTopmost(handle, at);
                };
                window.Show();
                return window;
            }

            var a = Square(layered, transparent: true);
            var b = Square(opaque, transparent: false);
            a.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => shown.Set());
            System.Windows.Threading.Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        shown.Wait(TimeSpan.FromSeconds(5));
        Thread.Sleep(500);

        var failures = 0;
        var screenshot = ScreenGrabber.Grab(area);
        failures += CheckAbsent(screenshot, layered.Offset(-area.X, -area.Y), "GDI screenshot, layered window");
        failures += CheckAbsent(screenshot, opaque.Offset(-area.X, -area.Y), "GDI screenshot, opaque window");

        var paths = AppPaths.FromEnvironment();
        var (result, ffmpeg) = RecordFor(["--region", $"{area.X},{area.Y},{area.Width},{area.Height}"], 1.5, 30, paths);
        System.Windows.Threading.Dispatcher.FromThread(ui)?.InvokeShutdown();
        if (!result.Succeeded)
        {
            Console.WriteLine($"FAIL recording: {result.Error}");
            return 1;
        }

        var output = Path.Combine(paths.DefaultVideosFolder, $"grably-exclusion-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        ScreenRecorder.SaveAsMp4Async(ffmpeg, result.Session, result.SessionFolder, output).GetAwaiter().GetResult();
        var frame = ExtractFrame(ffmpeg, output, 0.8);
        failures += CheckAbsent(frame, layered.Offset(-area.X, -area.Y), "recording, layered window");
        failures += CheckAbsent(frame, opaque.Offset(-area.X, -area.Y), "recording, opaque window");
        File.Delete(output);
        Directory.Delete(result.SessionFolder, recursive: true);
        Console.WriteLine(failures == 0 ? "PASS exclusion-test: Grably's windows stay out of screenshots and recordings" : $"FAIL exclusion-test: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private static int CheckAbsent(PixelBuffer image, PixelRect square, string what)
    {
        var magenta = 0;
        for (var y = square.Y + 10; y < square.Bottom - 10; y += 4)
        {
            for (var x = square.X + 10; x < square.Right - 10; x += 4)
            {
                var rgb = image.GetRgb(x, y);
                if (((rgb >> 16) & 0xFF) > 220 && ((rgb >> 8) & 0xFF) < 40 && (rgb & 0xFF) > 220)
                {
                    magenta++;
                }
            }
        }

        return Check(magenta == 0, $"{what}: excluded ({magenta} magenta samples)");
    }

    /// <summary>
    /// effects-test: records the 1280x720 test pattern with a scripted pointer (highlight), a click (ring), a shortcut
    /// (caption) and a generated camera picture in a circle, then checks each one in frames taken from the video.
    /// Nothing touches the real mouse, keyboard or camera.
    /// </summary>
    public static int EffectsTest(string[] args)
    {
        var paths = AppPaths.FromEnvironment();
        var cursor = new PixelPoint(1000, 650); // over the dark strip, away from the moving square
        var webcam = new PixelRect(1040, 60, 200, 200); // over the blue and black bars
        ScriptedInput[] script =
        [
            new(TimeSpan.FromSeconds(0.2), cursor),
            new(TimeSpan.FromSeconds(1.0), cursor, Click: Core.Recording.MouseButtonKind.Left),
            new(TimeSpan.FromSeconds(2.0), cursor, Key: 0x53, Modifiers: Core.Hotkeys.HotkeyModifiers.Control | Core.Hotkeys.HotkeyModifiers.Shift),
        ];
        var ffmpeg = FfmpegLocator.Find();
        var logger = new ConsoleLogger();
        var recorder = ScreenRecorder.StartAsync(
            new RecorderOptions
            {
                PatternSize = (1280, 720),
                Ffmpeg = ffmpeg,
                SessionsFolder = paths.SessionsFolder,
                HighlightCursor = true,
                ShowClicks = true,
                Keystrokes = Core.Settings.KeystrokeMode.ShortcutsOnly,
                Webcam = new SyntheticWebcam(),
                WebcamScreenRect = () => webcam,
                TestInput = script,
            },
            new EncoderProbe(ffmpeg, logger),
            logger).GetAwaiter().GetResult();
        Thread.Sleep(TimeSpan.FromSeconds(3) - recorder.Elapsed);
        var result = recorder.StopAsync().GetAwaiter().GetResult();
        if (!result.Succeeded)
        {
            Console.WriteLine($"FAIL recording: {result.Error}");
            return 1;
        }

        var output = Path.Combine(paths.DefaultVideosFolder, $"grably-effects-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        ScreenRecorder.SaveAsMp4Async(ffmpeg, result.Session, result.SessionFolder, output).GetAwaiter().GetResult();
        Directory.Delete(result.SessionFolder, recursive: true);

        var failures = 0;
        var highlight = ExtractFrame(ffmpeg, output, 0.8);
        var lit = highlight.GetRgb(cursor.X + 24, cursor.Y); // on the ring around the pointer
        var plain = highlight.GetRgb(cursor.X + 80, cursor.Y);
        var centre = highlight.GetRgb(cursor.X, cursor.Y);
        failures += Check(Luma(lit) > Luma(plain) + 25 && ((lit >> 16) & 0xFF) > (lit & 0xFF) + 20,
            $"pointer highlight: #{lit:X6} on the ring, #{plain:X6} beside it");
        failures += Check(Math.Abs(Luma(centre) - Luma(plain)) < 20, $"the pointer itself is not tinted (#{centre:X6} in the middle)");

        var ring = ExtractFrame(ffmpeg, output, 1.2); // 0.2 s after the click: the ring is about 21 px out
        var onRing = Enumerable.Range(0, 8)
            .Select(i => ring.GetRgb(cursor.X + (int)(21 * Math.Cos(i * Math.PI / 4)), cursor.Y + (int)(21 * Math.Sin(i * Math.PI / 4))))
            .Count(rgb => (rgb & 0xFF) > ((rgb >> 16) & 0xFF) + 25 && ((rgb >> 8) & 0xFF) > ((rgb >> 16) & 0xFF) + 25);
        failures += Check(onRing >= 4, $"click ring: {onRing} of 8 points on the circle are teal");

        var inside = ring.GetRgb(webcam.X + (webcam.Width / 2), webcam.Y + (webcam.Height / 2));
        var corner = ring.GetRgb(webcam.X + 6, webcam.Y + 6);
        failures += Check(Luma(inside) > 70, $"camera picture in the circle (#{inside:X6} at its centre, over a black bar)");
        failures += Check((corner & 0xFF) > 180 && ((corner >> 16) & 0xFF) < 70, $"outside the circle the bar shows through (#{corner:X6})");

        var caption = ExtractFrame(ffmpeg, output, 2.3);
        var bright = 0;
        for (var y = 600; y < 690; y++)
        {
            for (var x = 540; x < 740; x++)
            {
                bright += Luma(caption.GetRgb(x, y)) > 200 ? 1 : 0;
            }
        }

        failures += Check(bright > 150, $"keystroke caption: {bright} bright text pixels in the pill at the bottom");
        if (args.Contains("--keep"))
        {
            Console.WriteLine($"INFO kept {output}");
        }
        else
        {
            File.Delete(output);
        }

        Console.WriteLine(failures == 0 ? "PASS effects-test: highlight, click ring, caption and camera are in the video" : $"FAIL effects-test: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private static int Luma(int rgb) => ((((rgb >> 16) & 0xFF) * 3) + (((rgb >> 8) & 0xFF) * 6) + (rgb & 0xFF)) / 10;

    private static (RecordingResult Result, string Ffmpeg) RecordFor(
        string[] args, double seconds, int fps, AppPaths paths, IReadOnlyList<TimeSpan>? signals = null, TimeSpan? pauseAt = null)
    {
        var ffmpeg = FfmpegLocator.Find();
        var options = new RecorderOptions
        {
            Target = Target(args),
            FrameRate = fps,
            PreferNv12 = !args.Contains("--bgra"),
            Ffmpeg = ffmpeg,
            SessionsFolder = paths.SessionsFolder,
            SystemAudio = args.Contains("--system-audio"),
            Microphone = args.Contains("--mic"),
            TestSignals = signals,
        };
        var logger = new ConsoleLogger();
        var recorder = ScreenRecorder.StartAsync(options, new EncoderProbe(ffmpeg, logger), logger).GetAwaiter().GetResult();
        foreach (var warning in recorder.Warnings)
        {
            Console.WriteLine($"WARN {warning}");
        }

        var stop = new ManualResetEventSlim();
        recorder.SourceClosed += (_, _) => stop.Set();
        recorder.Failed += (_, _) => stop.Set();
        if (pauseAt is { } at)
        {
            // Pause for two seconds of wall time in the middle: the recording time (and its length) must not include it.
            stop.Wait(at - recorder.Elapsed);
            recorder.Pause();
            Thread.Sleep(2000);
            recorder.Resume();
        }

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

    private sealed record ProbeResult(string Codec, int Width, int Height, double FrameRate, double Duration, long Frames, string PixelFormat, string ColorSpace)
    {
        public string? AudioCodec { get; init; }
    }

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
        var audio = new ProcessStartInfo(info.FileName) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", video })
        {
            audio.ArgumentList.Add(a);
        }

        using var audioProcess = Process.Start(audio)!;
        var audioCodec = audioProcess.StandardOutput.ReadToEnd().Trim();
        audioProcess.WaitForExit();
        var rate = stream.GetProperty("avg_frame_rate").GetString()!.Split('/');
        return new ProbeResult(
            stream.GetProperty("codec_name").GetString()!,
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32(),
            double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture),
            double.Parse(json.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture),
            long.Parse(stream.GetProperty("nb_read_packets").GetString()!, CultureInfo.InvariantCulture),
            stream.GetProperty("pix_fmt").GetString()!,
            stream.TryGetProperty("color_space", out var space) ? space.GetString()! : "?")
        {
            AudioCodec = audioCodec.Length == 0 ? null : audioCodec,
        };
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
