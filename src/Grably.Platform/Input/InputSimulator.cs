using System.Runtime.InteropServices;
using Grably.Core.Hotkeys;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Grably.Platform.Input;

/// <summary>Synthesises keyboard input with SendInput. Used by self-tests in the dev harness.</summary>
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
