using System.Runtime.InteropServices;
using Grably.Core.Geometry;
using Grably.Core.Hotkeys;
using Grably.Core.Recording;
using Grably.Platform.Display;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Input;

/// <summary>
/// Mouse-button and key presses anywhere on the desktop for the recording's cursor effects, through Raw Input on a
/// message-only window with its own thread. Unlike low-level hooks, Raw Input is told about input after the fact:
/// nothing the user does ever waits for Grably (a pause in this process cannot stall typing, and Windows never
/// silently drops it). Used only while a recording with clicks or keys runs; keys are never logged or stored.
/// </summary>
public sealed class InputMonitor : IDisposable
{
    private const ushort GenericDesktopPage = 0x01;
    private const ushort MouseUsage = 0x02;
    private const ushort KeyboardUsage = 0x06;
    private const ushort LeftDown = 0x0001;
    private const ushort RightDown = 0x0004;
    private const ushort MiddleDown = 0x0010;
    private const ushort KeyBreak = 0x0001;
    private const ushort KeyE0 = 0x0002;
    private static int _classes;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly bool _mouse;
    private readonly bool _keyboard;
    private WNDPROC? _windowProc; // kept in a field: a collected window procedure crashes the process
    private HWND _window;
    private uint _threadId;
    private Exception? _startError;

    // Modifiers held, followed from the input itself (monitor thread only): by the time an event is handled the
    // keyboard has moved on, so asking it what is held now would be wrong for quick shortcuts.
    private bool _leftShift;
    private bool _rightShift;
    private bool _leftCtrl;
    private bool _rightCtrl;
    private bool _leftAlt;
    private bool _rightAlt;
    private bool _win;

    /// <param name="mouse">Report button presses.</param>
    /// <param name="keyboard">Report key presses.</param>
    public InputMonitor(bool mouse, bool keyboard)
    {
        _mouse = mouse;
        _keyboard = keyboard;
        _thread = new Thread(Run) { Name = "Grably input monitor", IsBackground = true };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
        {
            throw _startError;
        }
    }

    /// <summary>A mouse button went down where the pointer is (physical pixels). Raised on the monitor's thread.</summary>
    public event Action<PixelPoint, MouseButtonKind>? ButtonDown;

    /// <summary>
    /// A key went down, with the modifiers held and what the keyboard layout of the window in front types for it.
    /// Raised on the monitor's thread.
    /// </summary>
    public event Action<int, HotkeyModifiers, KeyText>? KeyDown;

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private unsafe void Run()
    {
        var className = $"GrablyInputMonitor{Interlocked.Increment(ref _classes)}";
        using var module = PInvoke.GetModuleHandle((string?)null);
        try
        {
            _threadId = PInvoke.GetCurrentThreadId();
            _windowProc = WindowProc;
            fixed (char* name = className)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _windowProc,
                    hInstance = (HINSTANCE)module.DangerousGetHandle(),
                    lpszClassName = name,
                };
                if (PInvoke.RegisterClassEx(windowClass) == 0)
                {
                    throw new InvalidOperationException($"The input window class could not be registered ({Marshal.GetLastWin32Error()}).");
                }

                var messageOnly = new HWND((void*)-3); // HWND_MESSAGE: a window that only receives messages
                _window = PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, messageOnly, null, module, null);
            }

            if (_window.IsNull)
            {
                throw new InvalidOperationException($"The input window could not be created ({Marshal.GetLastWin32Error()}).");
            }

            var devices = new List<RAWINPUTDEVICE>();
            if (_mouse)
            {
                devices.Add(new RAWINPUTDEVICE { usUsagePage = GenericDesktopPage, usUsage = MouseUsage, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK, hwndTarget = _window });
            }

            if (_keyboard)
            {
                devices.Add(new RAWINPUTDEVICE { usUsagePage = GenericDesktopPage, usUsage = KeyboardUsage, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK, hwndTarget = _window });
            }

            if (!PInvoke.RegisterRawInputDevices(devices.ToArray(), (uint)sizeof(RAWINPUTDEVICE)))
            {
                throw new InvalidOperationException($"Raw input could not be registered ({Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _startError = ex;
            Cleanup(className, module);
            _ready.Set();
            return;
        }

        // Whatever is already held when watching starts.
        _leftShift = Down(0xA0);
        _rightShift = Down(0xA1);
        _leftCtrl = Down(0xA2);
        _rightCtrl = Down(0xA3);
        _leftAlt = Down(0xA4);
        _rightAlt = Down(0xA5);
        _win = Down(0x5B) || Down(0x5C);
        _ready.Set();
        try
        {
            while (PInvoke.GetMessage(out var message, HWND.Null, 0, 0).Value > 0)
            {
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        finally
        {
            Cleanup(className, module);
        }
    }

    private unsafe void Cleanup(string className, SafeHandle module)
    {
        var remove = new List<RAWINPUTDEVICE>();
        if (_mouse)
        {
            remove.Add(new RAWINPUTDEVICE { usUsagePage = GenericDesktopPage, usUsage = MouseUsage, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE });
        }

        if (_keyboard)
        {
            remove.Add(new RAWINPUTDEVICE { usUsagePage = GenericDesktopPage, usUsage = KeyboardUsage, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE });
        }

        PInvoke.RegisterRawInputDevices(remove.ToArray(), (uint)sizeof(RAWINPUTDEVICE));
        if (!_window.IsNull)
        {
            PInvoke.DestroyWindow(_window);
            _window = HWND.Null;
        }

        PInvoke.UnregisterClass(className, module);
    }

    private unsafe LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_INPUT)
        {
            try
            {
                Handle((HRAWINPUT)lParam.Value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine(ex.GetType().Name); // never the input itself
            }
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private unsafe void Handle(HRAWINPUT input)
    {
        RAWINPUT data;
        var size = (uint)sizeof(RAWINPUT);
        if (PInvoke.GetRawInputData(input, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &data, &size, (uint)sizeof(RAWINPUTHEADER)) == unchecked((uint)-1))
        {
            return;
        }

        if (data.header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE)
        {
            var flags = data.data.mouse.Anonymous.Anonymous.usButtonFlags;
            var button = (flags & LeftDown) != 0 ? MouseButtonKind.Left
                : (flags & RightDown) != 0 ? MouseButtonKind.Right
                : (flags & MiddleDown) != 0 ? MouseButtonKind.Middle
                : (MouseButtonKind?)null;
            if (button is { } pressed)
            {
                ButtonDown?.Invoke(ScreenPointer.Position(), pressed);
            }
        }
        else if (data.header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEKEYBOARD)
        {
            var keyboard = data.data.keyboard;
            var down = (keyboard.Flags & KeyBreak) == 0;
            var right = (keyboard.Flags & KeyE0) != 0;
            switch (keyboard.VKey)
            {
                case 0x10: // Shift: left and right have their own scan codes
                    if (keyboard.MakeCode == 0x36)
                    {
                        _rightShift = down;
                    }
                    else
                    {
                        _leftShift = down;
                    }

                    return;
                case 0x11:
                    _ = right ? _rightCtrl = down : _leftCtrl = down;
                    return;
                case 0x12:
                    _ = right ? _rightAlt = down : _leftAlt = down;
                    return;
                case 0x5B or 0x5C:
                    _win = down;
                    return;
            }

            if (!down || keyboard.VKey is 0 or 0xFF)
            {
                return; // a key going up, or a fake key from a key combination
            }

            // AltGr arrives as left Ctrl plus right Alt; it types characters and is never a shortcut.
            var altGr = _rightAlt;
            var modifiers = HotkeyModifiers.None;
            modifiers |= _leftCtrl || _rightCtrl ? HotkeyModifiers.Control : 0;
            modifiers |= _leftAlt || _rightAlt ? HotkeyModifiers.Alt : 0;
            modifiers |= _leftShift || _rightShift ? HotkeyModifiers.Shift : 0;
            modifiers |= _win ? HotkeyModifiers.Windows : 0;
            KeyDown?.Invoke(keyboard.VKey, modifiers, new KeyText(Typed(keyboard.VKey, keyboard.MakeCode), altGr));
        }
    }

    private static bool Down(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// What the key types on the keyboard layout of the window in front, with the modifiers held now. Flag 4 keeps
    /// ToUnicodeEx from touching the keyboard state, so the user's dead keys (accents) still work.
    /// </summary>
    private unsafe string? Typed(ushort vk, ushort scanCode)
    {
        Span<byte> state = stackalloc byte[256];
        static byte Held(bool held) => held ? (byte)0x80 : (byte)0;
        state[0xA0] = Held(_leftShift);
        state[0xA1] = Held(_rightShift);
        state[0xA2] = Held(_leftCtrl);
        state[0xA3] = Held(_rightCtrl);
        state[0xA4] = Held(_leftAlt);
        state[0xA5] = Held(_rightAlt);
        state[0x10] = Held(_leftShift || _rightShift);
        state[0x11] = Held(_leftCtrl || _rightCtrl);
        state[0x12] = Held(_leftAlt || _rightAlt);

        state[0x14] = (byte)(PInvoke.GetKeyState(0x14) & 1); // Caps Lock
        var foreground = PInvoke.GetForegroundWindow();
        var layout = PInvoke.GetKeyboardLayout(PInvoke.GetWindowThreadProcessId(foreground, null));
        Span<char> text = stackalloc char[8];
        int count;
        fixed (byte* keys = state)
        fixed (char* buffer = text)
        {
            count = PInvoke.ToUnicodeEx(vk, scanCode, keys, new PWSTR(buffer), text.Length, 4, layout);
        }

        return count switch
        {
            > 0 => new string(text[..count]),
            < 0 => new string(text[..1]), // a dead key (an accent waiting for its letter): typing too
            _ => null,
        };
    }
}
