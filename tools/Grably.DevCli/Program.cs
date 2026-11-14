using System.Diagnostics;
using Grably.Core;
using Grably.Core.Hotkeys;
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
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            return Usage();
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
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
