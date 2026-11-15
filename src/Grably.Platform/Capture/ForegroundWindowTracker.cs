using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Grably.Platform.Capture;

/// <summary>
/// Remembers the last window the user worked in, ignoring Grably's own windows and the taskbar. Opening the tray
/// menu moves the foreground to the taskbar and then to Grably's menu, so "Capture window" from the tray uses this
/// instead of the current foreground window. Create it on a thread with a message loop (the UI thread).
/// </summary>
public sealed class ForegroundWindowTracker : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly WINEVENTPROC _callback; // kept in a field: the hook only holds a native pointer to it
    private readonly HWINEVENTHOOK _hook;
    private readonly int _ownProcessId = Environment.ProcessId;

    public ForegroundWindowTracker()
    {
        _callback = OnForegroundChanged;
        _hook = PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            HMODULE.Null, _callback, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);
        Remember(PInvoke.GetForegroundWindow());
    }

    /// <summary>The last foreground window that belongs to another app, or 0 if none was seen.</summary>
    public nint LastUserWindow { get; private set; }

    public void Dispose()
    {
        if (!_hook.IsNull)
        {
            PInvoke.UnhookWinEvent(_hook);
        }
    }

    private void OnForegroundChanged(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint thread, uint time) =>
        Remember(hwnd);

    private unsafe void Remember(HWND hwnd)
    {
        var window = WindowEnumerator.GetWindow((nint)hwnd.Value);
        if (window is not null && window.ProcessId != _ownProcessId && !ShellClasses.Contains(window.ClassName))
        {
            LastUserWindow = window.Handle;
        }
    }
}
