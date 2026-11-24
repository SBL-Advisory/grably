using System.Runtime.InteropServices;
using Grably.Core.Geometry;
using Grably.Core.Hotkeys;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Grably.Platform.Input;

/// <summary>Synthesises keyboard and mouse input. Used by the dev harness for end-to-end checks.</summary>
public static class InputSimulator
{
    // Keys that Windows expects with the extended-key flag (navigation cluster, PrintScreen, Win keys...).
    private static readonly HashSet<int> ExtendedKeys =
        [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x6F, 0x90];

    /// <summary>Presses and releases <paramref name="gesture"/>: modifiers down, key down, key up, modifiers up.</summary>
    /// <returns>The number of input events Windows accepted (all of them on success).</returns>
    public static int SendGesture(HotkeyGesture gesture)
    {
        var modifierKeys = new List<int>();
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            modifierKeys.Add(0x11);
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            modifierKeys.Add(0x12);
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            modifierKeys.Add(0x10);
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            modifierKeys.Add(0x5B);
        }

        var inputs = new List<INPUT>();
        inputs.AddRange(modifierKeys.Select(k => Key(k, up: false)));
        inputs.Add(Key(gesture.VirtualKey, up: false));
        inputs.Add(Key(gesture.VirtualKey, up: true));
        inputs.AddRange(Enumerable.Reverse(modifierKeys).Select(k => Key(k, up: true)));

        var array = inputs.ToArray();
        return (int)PInvoke.SendInput(array, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Puts the pointer on an exact physical pixel (the process must be per-monitor DPI aware).</summary>
    public static bool MoveTo(PixelPoint p) => PInvoke.SetCursorPos(p.X, p.Y);

    public static void LeftButton(bool down) => SendMouse(down ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP);

    public static void RightButton(bool down) => SendMouse(down ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP);

    public static void Click(PixelPoint p)
    {
        MoveTo(p);
        Thread.Sleep(30);
        LeftButton(down: true);
        Thread.Sleep(30);
        LeftButton(down: false);
    }

    /// <summary>Presses at <paramref name="from"/>, moves in steps to <paramref name="to"/> and releases.</summary>
    public static void Drag(PixelPoint from, PixelPoint to, int steps = 12)
    {
        MoveTo(from);
        Thread.Sleep(50);
        LeftButton(down: true);
        Thread.Sleep(50);
        for (var i = 1; i <= steps; i++)
        {
            MoveTo(new PixelPoint(from.X + ((to.X - from.X) * i / steps), from.Y + ((to.Y - from.Y) * i / steps)));
            Thread.Sleep(20);
        }

        Thread.Sleep(50);
        LeftButton(down: false);
    }

    /// <summary>Whether Ctrl, Shift, Alt or a Windows key is held down right now.</summary>
    public static bool AnyModifierDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>Turns the mouse wheel by <paramref name="notches"/> (negative scrolls down) over whatever is under the pointer.</summary>
    public static void Wheel(int notches) => SendMouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, notches * 120);

    private static void SendMouse(MOUSE_EVENT_FLAGS flags, int data = 0)
    {
        var input = new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = new INPUT._Anonymous_e__Union { mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data) } },
        };
        PInvoke.SendInput([input], Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(int virtualKey, bool up)
    {
        var flags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        if (ExtendedKeys.Contains(virtualKey))
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        }

        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                ki = new KEYBDINPUT
                {
                    wVk = (VIRTUAL_KEY)virtualKey,
                    wScan = (ushort)PInvoke.MapVirtualKey((uint)virtualKey, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                },
            },
        };
    }
}
