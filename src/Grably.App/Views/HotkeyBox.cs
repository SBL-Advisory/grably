using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grably.Core.Hotkeys;

namespace Grably.App.Views;

/// <summary>
/// A box that records a key combination: click it (or tab to it) and press the keys. Backspace or Delete clears it
/// (no hotkey), Esc leaves it unchanged. After a combination is recorded the box stops listening (click it again to
/// record another); the new combination takes effect when the settings are saved. <see cref="RecordingChanged"/>
/// tells the window when listening starts and stops, so Grably's own hotkeys can be released meanwhile.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(HotkeyGesture), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(HotkeyGesture.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).ShowState()));

    private bool _recording;

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox)); // the theme's TextBox look (implicit styles skip subclasses)
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
        Cursor = Cursors.Hand;
        MinWidth = 200;
        ShowState();
    }

    /// <summary>True when the box starts listening for keys, false when it stops. Always raised in pairs.</summary>
    public event EventHandler<bool>? RecordingChanged;

    public HotkeyGesture Gesture
    {
        get => (HotkeyGesture)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SetRecording(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetRecording(false);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (IsKeyboardFocused)
        {
            SetRecording(true); // clicking the focused box again records another combination
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab)
        {
            return; // let focus move on
        }

        e.Handled = true;
        if (!_recording)
        {
            if (key is Key.Enter or Key.Space)
            {
                SetRecording(true);
            }

            return;
        }

        switch (key)
        {
            case Key.Escape:
                SetRecording(false);
                return;
            case Key.Back or Key.Delete when Keyboard.Modifiers == ModifierKeys.None:
                Gesture = HotkeyGesture.None;
                SetRecording(false);
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin:
                Text = ModifierText(Modifiers()) + "...";
                return;
            case Key.Snapshot:
                return; // recorded on release (Windows does not always report the press)
            default:
                Record(key);
                return;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.Snapshot)
        {
            e.Handled = true;
            if (_recording)
            {
                Record(Key.Snapshot);
            }
        }
    }

    private void Record(Key key)
    {
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return;
        }

        var gesture = new HotkeyGesture(Modifiers(), vk);
        if (!gesture.IsSafeAsGlobalHotkey)
        {
            Text = "Add more keys, like Ctrl+Shift";
            return; // keep listening and keep the old combination
        }

        Gesture = gesture;
        SetRecording(false);
    }

    private void SetRecording(bool recording)
    {
        if (_recording == recording)
        {
            return;
        }

        _recording = recording;
        ShowState();
        RecordingChanged?.Invoke(this, recording);
    }

    private static HotkeyModifiers Modifiers()
    {
        var m = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            m |= HotkeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            m |= HotkeyModifiers.Alt;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            m |= HotkeyModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            m |= HotkeyModifiers.Windows;
        }

        return m;
    }

    /// <summary>"Ctrl+Shift+" for the modifiers held so far, in the same order and spelling as a gesture.</summary>
    private static string ModifierText(HotkeyModifiers modifiers) => string.Concat(
        modifiers.HasFlag(HotkeyModifiers.Control) ? "Ctrl+" : "",
        modifiers.HasFlag(HotkeyModifiers.Alt) ? "Alt+" : "",
        modifiers.HasFlag(HotkeyModifiers.Shift) ? "Shift+" : "",
        modifiers.HasFlag(HotkeyModifiers.Windows) ? "Win+" : "");

    private void ShowState() => Text = _recording ? "Press the keys..." : Gesture.IsNone ? "Not set" : Gesture.ToString();
}
