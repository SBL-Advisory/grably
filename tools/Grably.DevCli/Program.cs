using System.Diagnostics;
using Grably.Core;
using Grably.Core.Geometry;
using Grably.Core.Hotkeys;
using Grably.Core.Imaging;
using Grably.Platform.Capture;
using Grably.Platform.Display;
using Grably.Platform.Hotkeys;
using Grably.Platform.Input;

namespace Grably.DevCli;

/// <summary>
/// grably-dev: drives Grably features from the terminal so they can be verified without looking at the screen.
/// Every command prints a result line starting with PASS or FAIL where that applies, and exits non-zero on failure.
/// </summary>
internal static class Program
{
    private static readonly (string Name, string Help)[] Commands =
    [
        ("version", "Print the Grably version."),
        ("paths", "Print where settings, data and logs live (honours GRABLY_HOME)."),
        ("hotkey-selftest [gesture]", "Register a global hotkey, press it with SendInput, check it arrives. Default Ctrl+Alt+Shift+F24."),
        ("send-keys <gesture>", "Press a key combination with SendInput, e.g. to trigger a running Grably's hotkey."),
        ("monitors", "List monitors with physical bounds, work area and DPI."),
        ("windows", "List capturable top-level windows in z-order with their visible bounds."),
        ("screenshot <target> [--cursor] [--out f.png]", "Grab --all, --monitor N, --region x,y,w,h, --foreground or --window <title>."),
        ("mouse click x,y | drag x1,y1 x2,y2 | rightclick x,y  --require-window <title>",
            "Synthesise mouse input in physical pixels; refuses unless a window with that exact title is visible."),
        ("wait-window <title> [seconds]", "Wait until a window with that exact title is visible (default 5 s)."),
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            return Usage();
        }

        // Capture commands work in physical pixels; must happen before any window or metrics call.
        DpiAwareness.EnsurePerMonitorV2();
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "monitors" => ListMonitors(),
                "windows" => ListWindows(),
                "screenshot" => Screenshot(args[1..]),
                "mouse" => Mouse(args[1..]),
                "wait-window" when args.Length > 1 => WaitWindow(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5),
                "version" => PrintVersion(),
                "paths" => PrintPaths(),
                "hotkey-selftest" => HotkeySelfTest(args.Length > 1 ? args[1] : "Ctrl+Alt+Shift+F24"),
                "send-keys" when args.Length > 1 => SendKeys(args[1]),
                _ => Usage($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL {args[0]}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Usage(string? error = null)
    {
        if (error is not null)
        {
            Console.Error.WriteLine(error);
        }

        Console.WriteLine("Usage: grably-dev <command> [options]");
        Console.WriteLine();
        foreach (var (name, help) in Commands)
        {
            Console.WriteLine($"  {name,-28} {help}");
        }

        return error is null ? 0 : 2;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"{AppInfo.Name} {AppInfo.Version}");
        return 0;
    }

    private static int PrintPaths()
    {
        var paths = AppPaths.FromEnvironment();
        Console.WriteLine($"settings     {paths.SettingsFile}");
        Console.WriteLine($"local data   {paths.LocalRoot}");
        Console.WriteLine($"logs         {paths.LogsFolder}");
        Console.WriteLine($"library db   {paths.LibraryDatabase}");
        Console.WriteLine($"screenshots  {paths.DefaultScreenshotsFolder}");
        Console.WriteLine($"videos       {paths.DefaultVideosFolder}");
        return 0;
    }

    private static int ListMonitors()
    {
        Console.WriteLine($"virtual desktop {MonitorService.VirtualScreen}");
        foreach (var m in MonitorService.GetMonitors())
        {
            Console.WriteLine($"monitor {m.Index}  {m.Bounds}  work {m.WorkArea}  dpi {m.Dpi} ({m.Scale:P0})"
                + (m.IsPrimary ? "  primary" : string.Empty) + $"  {m.DeviceName}");
        }

        return 0;
    }

    private static int ListWindows()
    {
        foreach (var w in WindowEnumerator.GetVisibleWindows())
        {
            var title = w.Title.Length > 60 ? w.Title[..60] + "..." : w.Title;
            Console.WriteLine($"0x{w.Handle:X8}  {w.Bounds,-24} {w.ProcessName,-20} {w.ClassName,-28} {title}");
        }

        return 0;
    }

    private static int Screenshot(string[] args)
    {
        PixelRect? area = null;
        string label = "?";
        var output = "screenshot.png";
        var cursor = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all":
                    area = MonitorService.VirtualScreen;
                    label = "virtual desktop";
                    break;
                case "--monitor" when i + 1 < args.Length:
                    var index = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    var monitor = MonitorService.GetMonitors().FirstOrDefault(m => m.Index == index)
                        ?? throw new ArgumentException($"No monitor {index}; run 'grably-dev monitors'.");
                    area = monitor.Bounds;
                    label = $"monitor {index}";
                    break;
                case "--region" when i + 1 < args.Length:
                    area = PixelRect.TryParse(args[++i], out var region)
                        ? region
                        : throw new ArgumentException("--region expects x,y,width,height");
                    label = "region";
                    break;
                case "--foreground":
                    var foreground = WindowEnumerator.GetForegroundWindow()
                        ?? throw new InvalidOperationException("The foreground window cannot be captured.");
                    area = foreground.Bounds.Intersect(MonitorService.VirtualScreen);
                    label = $"window '{foreground.Title}'";
                    break;
                case "--window" when i + 1 < args.Length:
                    var text = args[++i];
                    var window = WindowEnumerator.GetVisibleWindows()
                        .FirstOrDefault(w => w.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"No visible window title contains '{text}'.");
                    area = window.Bounds.Intersect(MonitorService.VirtualScreen);
                    label = $"window '{window.Title}'";
                    break;
                case "--cursor":
                    cursor = true;
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown screenshot option '{args[i]}'.");
            }
        }

        if (area is not { IsEmpty: false } target)
        {
            throw new ArgumentException("Pick a target: --all, --monitor N, --region x,y,w,h, --foreground or --window <title>.");
        }

        var timer = Stopwatch.StartNew();
        var image = ScreenGrabber.Grab(target, cursor);
        var grabMs = timer.ElapsedMilliseconds;
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PngWriter.Save(image, path);
        Console.WriteLine($"PASS screenshot {label} {target} -> {path} ({image.Width}x{image.Height}, grab {grabMs} ms)");
        return 0;
    }

    private static int Mouse(string[] args)
    {
        var required = Array.IndexOf(args, "--require-window") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        if (required is null)
        {
            throw new ArgumentException("mouse needs --require-window <title> so it never clicks into an unexpected app.");
        }

        if (!IsWindowVisible(required))
        {
            Console.WriteLine($"FAIL mouse: no visible window titled '{required}'; nothing was clicked");
            return 1;
        }

        static PixelPoint Point(string text)
        {
            var parts = text.Split(',');
            return new PixelPoint(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        switch (args[0])
        {
            case "click":
                InputSimulator.Click(Point(args[1]));
                break;
            case "rightclick":
                InputSimulator.MoveTo(Point(args[1]));
                Thread.Sleep(30);
                InputSimulator.RightButton(down: true);
                InputSimulator.RightButton(down: false);
                break;
            case "drag":
                InputSimulator.Drag(Point(args[1]), Point(args[2]));
                break;
            default:
                throw new ArgumentException($"Unknown mouse action '{args[0]}'.");
        }

        Console.WriteLine($"PASS mouse {string.Join(' ', args.TakeWhile(a => a != "--require-window"))}");
        return 0;
    }

    private static int WaitWindow(string title, double seconds)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < seconds)
        {
            if (IsWindowVisible(title))
            {
                Console.WriteLine($"PASS window '{title}' visible after {deadline.ElapsedMilliseconds} ms");
                return 0;
            }

            Thread.Sleep(100);
        }

        Console.WriteLine($"FAIL window '{title}' not visible within {seconds} s");
        return 1;
    }

    private static bool IsWindowVisible(string title) =>
        WindowEnumerator.GetVisibleWindows().Any(w => w.Title == title);

    private static int SendKeys(string gestureText)
    {
        var gesture = HotkeyGesture.Parse(gestureText);
        var sent = InputSimulator.SendGesture(gesture);
        var expected = 2 + (2 * System.Numerics.BitOperations.PopCount((uint)gesture.Modifiers));
        Console.WriteLine(sent == expected
            ? $"PASS sent {gesture} ({sent} input events)"
            : $"FAIL sent {gesture}: Windows accepted {sent} of {expected} input events");
        return sent == expected ? 0 : 1;
    }

    private static int HotkeySelfTest(string gestureText)
    {
        var gesture = HotkeyGesture.Parse(gestureText);
        using var hotkeys = new GlobalHotkeyService();
        var received = new TaskCompletionSource<HotkeyAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        hotkeys.HotkeyPressed += (_, e) => received.TrySetResult(e.Action);

        var registration = hotkeys.Register(HotkeyAction.CaptureRegion, gesture);
        if (!registration.Succeeded)
        {
            Console.WriteLine($"FAIL hotkey {gesture}: RegisterHotKey failed with Win32 error {registration.ErrorCode}"
                + (registration.IsTakenByAnotherApp ? " (already registered by another app)" : string.Empty));
            return 1;
        }

        var timer = Stopwatch.StartNew();
        var sent = InputSimulator.SendGesture(gesture);
        var arrived = received.Task.Wait(TimeSpan.FromSeconds(3));
        if (!arrived)
        {
            Console.WriteLine($"FAIL hotkey {gesture}: sent {sent} input events but no WM_HOTKEY arrived within 3 s "
                + "(SendInput is blocked when the desktop is locked or a higher-integrity window has focus)");
            return 1;
        }

        Console.WriteLine($"PASS hotkey {gesture} received in {timer.ElapsedMilliseconds} ms");
        return 0;
    }
}
