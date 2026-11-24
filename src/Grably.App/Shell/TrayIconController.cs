using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Grably.Core.Hotkeys;
using Grably.Core.Settings;
using Grably.Platform.Shell;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace Grably.App.Shell;

/// <summary>
/// The notification-area icon and its menu. Picks the icon variant for the taskbar's light/dark theme and the
/// recording state, at the exact pixel size Windows uses for the current display scaling.
/// </summary>
public sealed class TrayIconController(
    AppCommands commands, SettingsStore settings, UserNotifications notifications, UpdateService updates, ILogger<TrayIconController> logger)
    : IDisposable
{
    private static readonly TimeSpan FastRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowRetry = TimeSpan.FromSeconds(30);
    private const int FastAttempts = 30;

    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _currentImage;
    private DispatcherTimer? _retry;
    private bool _recording;
    private bool _created;
    private int _attempts;

    public void Show()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "Grably",
            ContextMenu = BuildMenu(),
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => commands.OpenLibrary();
        UpdateImage();
        notifications.Requested += (_, message) => Notify(message);
        updates.Changed += OnUpdateReady;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        if (!TryCreate())
        {
            // At sign-in Grably can start before Explorer's notification area exists. Without the icon there is no
            // way to reach the menu, so keep trying: every 2 s for a minute, then every 30 s.
            _retry = new DispatcherTimer { Interval = FastRetry };
            _retry.Tick += (_, _) =>
            {
                if (TryCreate())
                {
                    _retry.Stop();
                }
                else if (_attempts == FastAttempts)
                {
                    logger.LogError("Tray icon still missing after {Attempts} attempts; retrying every {Interval}", _attempts, SlowRetry);
                    _retry.Interval = SlowRetry;
                }
            };
            _retry.Start();
        }
    }

    private bool TryCreate()
    {
        _attempts++;
        try
        {
            // ForceCreate(true) would also put the whole process into Windows Efficiency Mode, which throttles recording.
            _icon!.ForceCreate(enablesEfficiencyMode: false);
            _created = true;
            logger.LogInformation("Tray icon shown");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (_attempts <= FastAttempts)
            {
                logger.LogWarning(ex, "Could not add the tray icon (attempt {Attempt})", _attempts);
            }

            return false;
        }
    }

    /// <summary>Switches between the idle icon and the recording icon (red dot).</summary>
    public void SetRecording(bool recording)
    {
        _recording = recording;
        UpdateImage();
    }

    public void Notify(string message)
    {
        if (_icon is null || !_created)
        {
            logger.LogInformation("Notification not shown (no tray icon yet): {Message}", message);
            return;
        }

        try
        {
            _icon.ShowNotification("Grably", message, NotificationIcon.None);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not show a tray notification");
        }
    }

    public void Dispose()
    {
        _retry?.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        updates.Changed -= OnUpdateReady;
        _icon?.Dispose();
        _icon = null;
        _currentImage?.Dispose();
    }

    /// <summary>The menu offers Restart to update instead of Check for updates.</summary>
    private void OnUpdateReady(object? sender, EventArgs e)
    {
        if (_icon is not null)
        {
            _icon.ContextMenu = BuildMenu();
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Light/dark taskbar switches arrive as the General category (ImmersiveColorSet).
        if (e.Category == UserPreferenceCategory.General)
        {
            Application.Current?.Dispatcher.BeginInvoke(UpdateImage);
        }
    }

    private void UpdateImage()
    {
        if (_icon is null)
        {
            return;
        }

        var state = _recording ? "recording" : "idle";
        var theme = WindowsTheme.IsTaskbarLight() ? "light" : "dark";
        var uri = new Uri($"pack://application:,,,/Assets/Tray/tray-{state}-{theme}.ico");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        var size = WindowsTheme.SmallIconSize();
        var image = new System.Drawing.Icon(stream, size, size);

        _icon.Icon = image;
        _currentImage?.Dispose();
        _currentImage = image;
    }

    private ContextMenu BuildMenu()
    {
        var hotkeys = settings.Current.Hotkeys;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Capture region", SymbolRegular.Screenshot24, commands.CaptureRegion, hotkeys, HotkeyAction.CaptureRegion));
        menu.Items.Add(Item("Capture window", SymbolRegular.Window24, commands.CaptureLastUserWindow, hotkeys, HotkeyAction.CaptureActiveWindow));
        menu.Items.Add(Item("Capture full screen", SymbolRegular.Desktop24, commands.CaptureFullScreen, hotkeys, HotkeyAction.CaptureFullScreen));
        menu.Items.Add(Item("Scrolling capture", SymbolRegular.ArrowAutofitHeight24, commands.ScrollingCapture, hotkeys, HotkeyAction.ScrollingCapture));
        menu.Items.Add(Item("Capture text", SymbolRegular.ScanText24, commands.CaptureText, hotkeys, HotkeyAction.CaptureText));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Record screen", SymbolRegular.Record24, commands.ToggleRecording, hotkeys, HotkeyAction.ToggleRecording));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open library", SymbolRegular.Library24, commands.OpenLibrary));
        menu.Items.Add(Item("Settings", SymbolRegular.Settings24, commands.OpenSettings));
        menu.Items.Add(updates.ReadyVersion is { } version
            ? Item($"Restart to update to {version}", SymbolRegular.ArrowCounterclockwise24, commands.RestartToUpdate)
            : Item("Check for updates", SymbolRegular.ArrowSync24, commands.CheckForUpdates));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", SymbolRegular.Dismiss24, commands.Exit));
        return menu;
    }

    private static MenuItem Item(
        string header, SymbolRegular symbol, Action action,
        IReadOnlyDictionary<HotkeyAction, HotkeyGesture>? hotkeys = null, HotkeyAction? hotkey = null)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon { Symbol = symbol } };
        if (hotkeys is not null && hotkey is { } key && hotkeys.TryGetValue(key, out var gesture) && !gesture.IsNone)
        {
            item.InputGestureText = gesture.ToString();
        }

        item.Click += async (_, _) =>
        {
            // Let the menu close and the screen repaint first, or a capture would freeze the menu into the image.
            await Task.Delay(AppCommands.WindowHideDelay);
            action();
        };
        return item;
    }
}
