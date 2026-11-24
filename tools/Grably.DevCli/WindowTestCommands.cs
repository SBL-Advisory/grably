using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Grably.Core;
using Grably.Core.Imaging;
using Grably.Core.Recording;
using Grably.Platform.Capture;
using Grably.Platform.Recording;

namespace Grably.DevCli;

/// <summary>grably-dev window-test.</summary>
internal static class WindowTestCommands
{
    private const string Title = "Grably window test";

    /// <summary>
    /// window-test [--keep]: records a test window of its own (colour bars) while it grows, is minimised, is restored
    /// and is closed. Checks the recording ends when the window closes, keeps its length through the minimised
    /// stretch, and shows the bars before, during and after each change. The window is never activated and nothing is
    /// clicked or typed; it shows on screen for about seven seconds.
    /// </summary>
    public static int WindowTest(string[] args)
    {
        var paths = AppPaths.FromEnvironment();
        Directory.CreateDirectory(paths.DefaultVideosFolder);
        var ffmpeg = FfmpegLocator.Find();
        var output = Path.Combine(paths.DefaultVideosFolder, $"grably-window-test-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

        using var window = TestWindow.Open();
        var info = WindowEnumerator.GetVisibleWindows().FirstOrDefault(w => w.Handle == window.Handle)
            ?? throw new InvalidOperationException("The test window did not show up.");
        var logger = new RecordingCommands.ConsoleLogger();
        var options = new RecorderOptions
        {
            Target = CaptureTarget.ForWindow(info),
            FrameRate = 30,
            PreferNv12 = true,
            Ffmpeg = ffmpeg,
            SessionsFolder = paths.SessionsFolder,
        };
        var recorder = ScreenRecorder.StartAsync(options, new EncoderProbe(ffmpeg, logger), logger).GetAwaiter().GetResult();
        using var closed = new ManualResetEventSlim();
        recorder.SourceClosed += (_, _) => closed.Set();

        void At(double seconds, string what, Action<Window> change)
        {
            var wait = TimeSpan.FromSeconds(seconds) - recorder.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }

            Console.WriteLine($"INFO {recorder.Elapsed.TotalSeconds:0.00} s: {what}");
            window.Invoke(change);
        }

        At(1.5, "the window grows", w => (w.Width, w.Height) = (w.Width * 1.4, w.Height * 1.3));
        // Through ShowWindow, so restoring never activates the window and takes the keyboard from whatever has it.
        At(3.0, "minimised", _ => ShowWindow(window.Handle, ShowMinimizedNoActivate));
        At(4.5, "restored", _ => ShowWindow(window.Handle, ShowNoActivate));
        At(6.0, "closed", w => w.Close());
        var endedOnClose = closed.Wait(TimeSpan.FromSeconds(5));
        var result = recorder.StopAsync().GetAwaiter().GetResult();
        recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (!result.Succeeded)
        {
            Console.WriteLine($"FAIL recording: {result.Error}");
            return 1;
        }

        if (ScreenRecorder.SaveAsMp4Async(ffmpeg, result.Session, result.SessionFolder, output).GetAwaiter().GetResult() is { } saveError)
        {
            Console.WriteLine($"FAIL {saveError}");
            return 1;
        }

        Directory.Delete(result.SessionFolder, recursive: true);
        var probe = RecordingCommands.Probe(ffmpeg, output);
        Console.WriteLine($"INFO {probe.Codec} {probe.Width}x{probe.Height}, {probe.Duration:0.000} s, {probe.Frames} frames");
        var failures = Check(endedOnClose, "closing the window ends the recording");
        failures += Check(Math.Abs(probe.Duration - 6.0) <= 0.6, $"it lasts until the window closed ({probe.Duration:0.00} s, closed at 6 s), minimised time included");
        foreach (var (at, when) in new[] { (1.0, "at first"), (2.4, "after it grew"), (3.8, "while minimised (the last picture stays)"), (5.4, "after it was restored") })
        {
            var bars = VisibleBars(RecordingCommands.ExtractFrame(ffmpeg, output, at));
            failures += Check(bars >= 7, $"{when}: {bars} of 8 colour bars in the frame at {at:0.0} s");
        }

        if (args.Contains("--keep"))
        {
            Console.WriteLine($"INFO kept {output}");
        }
        else
        {
            File.Delete(output);
        }

        Console.WriteLine(failures == 0 ? "PASS window-test: growing, minimising, restoring and closing are all handled" : $"FAIL window-test: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private const int ShowNoActivate = 4;
    private const int ShowMinimizedNoActivate = 7;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    /// <summary>How many of the test bars' colours appear along a row through the bars (scaled or letterboxed).</summary>
    private static int VisibleBars(PixelBuffer frame)
    {
        var y = frame.Height * 3 / 5;
        return TestPattern.Bars.Count(bar => Enumerable.Range(0, frame.Width).Any(x => Close(frame.GetRgb(x, y), bar)));
    }

    private static bool Close(int a, int b) =>
        Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) <= 24
        && Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) <= 24
        && Math.Abs((a & 0xFF) - (b & 0xFF)) <= 24;

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
        return ok ? 0 : 1;
    }

    /// <summary>A window with the test bars on a thread of its own, so the test can drive it while it records.</summary>
    private sealed class TestWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _shown = new();
        private Dispatcher? _dispatcher;
        private Window? _window;

        private TestWindow()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public nint Handle { get; private set; }

        public static TestWindow Open()
        {
            var window = new TestWindow();
            window._thread.Start();
            if (!window._shown.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException("The test window did not open.");
            }

            Thread.Sleep(500); // let it paint before recording starts
            return window;
        }

        public void Invoke(Action<Window> action) => _dispatcher!.Invoke(() => action(_window!));

        public void Dispose()
        {
            _dispatcher?.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
            _shown.Dispose();
        }

        private void Run()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            var bars = new UniformGrid { Rows = 1 };
            foreach (var rgb in TestPattern.Bars)
            {
                bars.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)) });
            }

            _window = new Window
            {
                Title = Title,
                Width = 640,
                Height = 400,
                Left = 120,
                Top = 120,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                Content = bars,
            };
            _window.SourceInitialized += (_, _) => Handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            _window.ContentRendered += (_, _) => _shown.Set();
            _window.Show();
            Dispatcher.Run();
        }
    }
}
