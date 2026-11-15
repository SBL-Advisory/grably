using System.Runtime.InteropServices;
using Grably.Core.Hotkeys;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Grably.Platform.Hotkeys;

/// <summary>Outcome of registering one hotkey with Windows.</summary>
public sealed record HotkeyRegistration(HotkeyAction Action, HotkeyGesture Gesture, bool Succeeded, int ErrorCode)
{
    /// <summary>Win32 ERROR_HOTKEY_ALREADY_REGISTERED: another app (or Windows itself) owns the combination.</summary>
    public bool IsTakenByAnotherApp => ErrorCode == 1409;
}

public sealed class HotkeyPressedEventArgs(HotkeyAction action) : EventArgs
{
    public HotkeyAction Action { get; } = action;
}

/// <summary>
/// System-wide hotkeys through RegisterHotKey on a dedicated thread with its own message loop, so they work no
/// matter which window (if any) Grably has open. <see cref="HotkeyPressed"/> is raised on that thread; handlers
/// must return quickly and marshal to the UI thread themselves. All methods are thread-safe.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WmInvoke = PInvoke.WM_APP + 1;

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Queue<WorkItem> _work = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();

    // Owned by the message-loop thread.
    private readonly Dictionary<int, Entry> _registered = [];
    private int _nextId = 1;

    private uint _threadId;
    private int _managedThreadId;
    private bool _loopEnded; // guarded by _gate
    private bool _disposed;

    public GlobalHotkeyService(ILogger<GlobalHotkeyService>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Grably hotkeys" };
        _thread.Start();
        _ready.Wait();
    }

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>
    /// Replaces the hotkeys registered by the previous <see cref="Apply"/> with <paramref name="bindings"/>.
    /// Registrations made with <see cref="Register"/> are left alone. Turned-off gestures are skipped.
    /// Returns one result per attempted registration.
    /// </summary>
    public IReadOnlyList<HotkeyRegistration> Apply(IEnumerable<KeyValuePair<HotkeyAction, HotkeyGesture>> bindings)
    {
        var list = bindings.Where(b => !b.Value.IsNone).ToList();
        return Invoke(() =>
        {
            UnregisterOnLoop(e => e.FromApply);
            return list.Select(b => RegisterOnLoop(b.Key, b.Value, fromApply: true).Registration).ToList();
        });
    }

    /// <summary>Registers one more hotkey that survives later <see cref="Apply"/> calls (e.g. pause while recording).</summary>
    public HotkeyRegistration Register(HotkeyAction action, HotkeyGesture gesture) =>
        Invoke(() => RegisterOnLoop(action, gesture, fromApply: false).Registration);

    /// <summary>Removes the registrations for <paramref name="action"/> made with <see cref="Register"/>.</summary>
    public void Unregister(HotkeyAction action) =>
        Invoke(() => UnregisterOnLoop(e => !e.FromApply && e.Callback is null && e.Registration.Action == action));

    /// <summary>
    /// Grabs a key combination for a short time (e.g. Esc while the capture overlay is up, so it works even when the
    /// overlay could not take the keyboard focus). <paramref name="onPressed"/> runs on the hotkey thread.
    /// Dispose the result to release the key. Returns null when the combination is taken.
    /// </summary>
    public IDisposable? RegisterTemporary(HotkeyGesture gesture, Action onPressed)
    {
        var (registration, id) = Invoke(() => RegisterOnLoop(default, gesture, fromApply: false, onPressed));
        return registration.Succeeded ? new TemporaryRegistration(this, id) : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private T Invoke<T>(Func<T> func)
    {
        // Already on the hotkey thread (e.g. a HotkeyPressed handler): posting and waiting would deadlock.
        if (Environment.CurrentManagedThreadId == _managedThreadId)
        {
            return func();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(
            () =>
            {
                try
                {
                    done.SetResult(func());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            },
            ex => done.TrySetException(ex));

        lock (_gate)
        {
            // Checked under the lock the loop takes when it ends, so work is either run or failed, never stranded.
            if (_disposed || _loopEnded)
            {
                throw new ObjectDisposedException(nameof(GlobalHotkeyService));
            }

            // Post before queueing: the loop can only dequeue under this lock, so the item is still picked up, and a
            // failed post (full message queue) leaves nothing behind for a caller to wait on.
            if (!PInvoke.PostThreadMessage(_threadId, WmInvoke, default, default))
            {
                throw new InvalidOperationException(
                    $"Could not reach the hotkey thread (Win32 error {Marshal.GetLastPInvokeError()}).");
            }

            _work.Enqueue(item);
        }

        return done.Task.GetAwaiter().GetResult();
    }

    private (HotkeyRegistration Registration, int Id) RegisterOnLoop(
        HotkeyAction action, HotkeyGesture gesture, bool fromApply, Action? callback = null)
    {
        var id = _nextId++;
        var modifiers = (HOT_KEY_MODIFIERS)gesture.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        var ok = PInvoke.RegisterHotKey(HWND.Null, id, modifiers, (uint)gesture.VirtualKey);
        var error = ok ? 0 : Marshal.GetLastPInvokeError();
        var result = new HotkeyRegistration(action, gesture, ok, error);
        var purpose = callback is null ? action.ToString() : "a temporary use";
        if (ok)
        {
            _registered[id] = new Entry(result, fromApply, callback);
            _logger.LogInformation("Registered hotkey {Gesture} for {Purpose}", gesture, purpose);
        }
        else
        {
            _logger.LogWarning("Could not register hotkey {Gesture} for {Purpose} (Win32 error {Error})", gesture, purpose, error);
        }

        return (result, id);
    }

    private bool UnregisterOnLoop(Func<Entry, bool> which)
    {
        foreach (var id in _registered.Where(p => which(p.Value)).Select(p => p.Key).ToList())
        {
            PInvoke.UnregisterHotKey(HWND.Null, id);
            _registered.Remove(id);
        }

        return true;
    }

    private void MessageLoop()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        _managedThreadId = Environment.CurrentManagedThreadId;

        // Make sure the thread has a message queue before anyone posts to it.
        PInvoke.PeekMessage(out _, HWND.Null, PInvoke.WM_USER, PInvoke.WM_USER, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        _ready.Set();

        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0).Value > 0)
        {
            if (msg.message == PInvoke.WM_HOTKEY)
            {
                if (_registered.TryGetValue((int)msg.wParam.Value, out var entry))
                {
                    if (entry.Callback is { } callback)
                    {
                        RunCallback(callback, entry.Registration.Gesture);
                    }
                    else
                    {
                        _logger.LogInformation("Hotkey {Gesture} pressed ({Action})", entry.Registration.Gesture, entry.Registration.Action);
                        RaisePressed(entry.Registration.Action);
                    }
                }
            }
            else if (msg.message == WmInvoke)
            {
                RunQueuedWork();
            }
        }

        UnregisterOnLoop(_ => true);

        List<WorkItem> stranded;
        lock (_gate)
        {
            _loopEnded = true;
            stranded = [.. _work];
            _work.Clear();
        }

        foreach (var item in stranded)
        {
            item.Fail(new ObjectDisposedException(nameof(GlobalHotkeyService)));
        }
    }

    private void RunQueuedWork()
    {
        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                if (!_work.TryDequeue(out item!))
                {
                    return;
                }
            }

            item.Run();
        }
    }

    private void RaisePressed(HotkeyAction action)
    {
        try
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(action));
        }
        catch (Exception ex)
        {
            // Never let a handler take down the message loop and with it every hotkey.
            _logger.LogError(ex, "Hotkey handler for {Action} failed", action);
        }
    }

    private void RunCallback(Action callback, HotkeyGesture gesture)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temporary hotkey {Gesture} handler failed", gesture);
        }
    }

    private sealed record Entry(HotkeyRegistration Registration, bool FromApply, Action? Callback);

    private sealed record WorkItem(Action Run, Action<Exception> Fail);

    private sealed class TemporaryRegistration(GlobalHotkeyService owner, int id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            try
            {
                owner.Invoke(() => owner.UnregisterOnLoop(e => e.Callback is not null && owner.IsId(e, id)));
            }
            catch (ObjectDisposedException)
            {
                // The service is shutting down and releases every hotkey itself.
            }
        }
    }

    private bool IsId(Entry entry, int id) => _registered.TryGetValue(id, out var found) && ReferenceEquals(found, entry);
}
