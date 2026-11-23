using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Grably.Core.Geometry;
using Grably.Platform.Display;
using Grably.Platform.Recording;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Grably.App.Recording;

/// <summary>Placement and capture exclusion shared by the recording windows (all of them stay out of the video).</summary>
internal static class RecordingChrome
{
    /// <summary>Excludes the window from every capture and places it with <paramref name="place"/> once it has a size.</summary>
    /// <param name="keepPlace">True once the user has moved the window: a DPI change then leaves it where they put it.</param>
    public static void Prepare(Window window, Func<int, int, PixelRect> place, Func<bool>? keepPlace = null)
    {
        window.SourceInitialized += (_, _) => WindowActions.ExcludeFromCapture(new WindowInteropHelper(window).Handle);
        void Place()
        {
            var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            var bounds = place((int)Math.Ceiling(window.ActualWidth * scale), (int)Math.Ceiling(window.ActualHeight * scale));
            ScreenPointer.PlaceTopmost(new WindowInteropHelper(window).Handle, bounds);
        }

        window.Loaded += (_, _) => Place();
        window.DpiChanged += (_, _) =>
        {
            if (keepPlace?.Invoke() != true)
            {
                window.Dispatcher.BeginInvoke(Place);
            }
        };
    }

    /// <summary>Below the area if there is room on its monitor, else above it, else inside its bottom edge.</summary>
    public static PixelRect Beside(PixelRect area, PixelRect workArea, int width, int height, int gap = 10)
    {
        var x = Math.Clamp(area.X + ((area.Width - width) / 2), workArea.X, Math.Max(workArea.X, workArea.Right - width));
        var y = area.Bottom + gap + height <= workArea.Bottom ? area.Bottom + gap
            : area.Y - gap - height >= workArea.Y ? area.Y - gap - height
            : area.Bottom - gap - height;
        return new PixelRect(x, y, width, height);
    }

    public static Window Panel(string title) => new()
    {
        Title = title,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        Topmost = true,
        SizeToContent = SizeToContent.WidthAndHeight,

        // The theme's window style has a minimum size meant for app windows; these are slim bars.
        MinWidth = 0,
        MinHeight = 0,
    };

    public static Border Card(UIElement content)
    {
        var border = new Border { Padding = new Thickness(12, 8, 8, 8), BorderThickness = new Thickness(1), Child = content };
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        border.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        return border;
    }

    public static Button IconButton(SymbolRegular symbol, string tip, ControlAppearance appearance = ControlAppearance.Transparent) =>
        new() { Icon = new SymbolIcon { Symbol = symbol }, ToolTip = tip, Appearance = appearance, Margin = new Thickness(2, 0, 2, 0) };
}

/// <summary>A dashed outline just outside the area being recorded (so it is never in the video even without exclusion).</summary>
internal sealed class RegionFrameWindow : Window
{
    private const int Gap = 4;

    public RegionFrameWindow(PixelRect area)
    {
        Title = "Grably recording area";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true; // transparent pixels let clicks through to the apps being recorded
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        MinWidth = 0; // a small area's outline must not be stretched to the theme's minimum window size
        MinHeight = 0;
        var outline = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x38, 0x3B)),
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
        };
        Content = outline;
        var outer = new PixelRect(area.X - Gap, area.Y - Gap, area.Width + (2 * Gap), area.Height + (2 * Gap));
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            WindowActions.ExcludeFromCapture(handle);
            ScreenPointer.PlaceTopmost(handle, outer);
        };
    }
}

/// <summary>What the user chose in the pre-record bar.</summary>
internal sealed record PreRecordChoice(bool SystemAudio, bool Microphone, int FrameRate);

/// <summary>The bar shown before recording starts: sound sources (with a live microphone level), frame rate, Record.</summary>
internal sealed class PreRecordWindow
{
    private readonly Window _window;
    private readonly TaskCompletionSource<PreRecordChoice?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ToggleSwitch _system;
    private readonly ToggleSwitch _microphone;
    private readonly ProgressBar _level = new() { Width = 56, Height = 4, Maximum = 1, Margin = new Thickness(6, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _fps = new() { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly string? _microphoneId;
    private MicrophoneLevelMeter? _meter;

    public PreRecordWindow(PixelRect area, PixelRect workArea, PreRecordChoice defaults, string? microphoneId)
    {
        _microphoneId = microphoneId;
        _window = RecordingChrome.Panel("Grably: ready to record");
        _system = new ToggleSwitch { Content = "PC sound", IsChecked = defaults.SystemAudio, Margin = new Thickness(0, 0, 16, 0) };
        _microphone = new ToggleSwitch { Content = "Microphone", IsChecked = defaults.Microphone };
        foreach (var rate in new[] { 15, 30, 60 })
        {
            _fps.Items.Add(new ComboBoxItem { Content = $"{rate} fps", Tag = rate, IsSelected = rate == defaults.FrameRate });
        }

        var record = RecordingChrome.IconButton(SymbolRegular.RecordStop24, "Start recording (Enter)", ControlAppearance.Primary);
        record.Content = "Record";
        record.Click += (_, _) => Finish(true);
        var cancel = RecordingChrome.IconButton(SymbolRegular.DismissCircle24, "Cancel (Esc)");
        cancel.Click += (_, _) => Finish(false);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var element in new UIElement[] { _system, _microphone, _level, _fps, record, cancel })
        {
            row.Children.Add(element);
        }

        _window.Content = RecordingChrome.Card(row);
        RecordingChrome.Prepare(_window, (w, h) => RecordingChrome.Beside(area, workArea, w, h));
        _microphone.Checked += (_, _) => StartMeter();
        _microphone.Unchecked += (_, _) => StopMeter();
        _window.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                Finish(e.Key == Key.Enter);
            }
        };
        _window.Closed += (_, _) =>
        {
            StopMeter();
            _result.TrySetResult(null);
        };
    }

    /// <summary>The window itself (UI snapshots).</summary>
    internal Window Window => _window;

    /// <summary>Closes the bar as if Cancel was chosen (the recording flow is being abandoned).</summary>
    public void Cancel()
    {
        if (_window.IsLoaded)
        {
            _window.Close();
        }

        _result.TrySetResult(null);
    }

    /// <summary>Shows the bar; the choice, or null when cancelled.</summary>
    public Task<PreRecordChoice?> ShowAsync()
    {
        _window.Show();
        _window.Activate();
        if (_microphone.IsChecked == true)
        {
            StartMeter();
        }

        return _result.Task;
    }

    private void Finish(bool record)
    {
        var choice = new PreRecordChoice(
            _system.IsChecked == true,
            _microphone.IsChecked == true,
            (_fps.SelectedItem as ComboBoxItem)?.Tag as int? ?? 30);
        _result.TrySetResult(record ? choice : null);
        _window.Close();
    }

    private void StartMeter()
    {
        StopMeter();
        try
        {
            _meter = new MicrophoneLevelMeter(_microphoneId);
            var last = DateTime.MinValue;
            _meter.Level += (_, level) =>
            {
                if ((DateTime.UtcNow - last).TotalMilliseconds < 50)
                {
                    return;
                }

                last = DateTime.UtcNow;
                _window.Dispatcher.BeginInvoke(() => _level.Value = Math.Max(level, _level.Value * 0.7));
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No microphone, or access to it is turned off in Windows privacy settings.
            _microphone.IsChecked = false;
            _microphone.Content = "No microphone";
        }
    }

    private void StopMeter()
    {
        _meter?.Dispose();
        _meter = null;
        _level.Value = 0;
    }
}

/// <summary>3, 2, 1 over the middle of the area. Esc cancels.</summary>
internal sealed class CountdownWindow : Window
{
    private readonly TextBlock _number = new()
    {
        FontSize = 72,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public CountdownWindow(PixelRect area)
    {
        Title = "Grably countdown";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        MinWidth = 0; // the theme's window style has a minimum size meant for app windows
        MinHeight = 0;
        Width = 160;
        Height = 160;
        Content = new Grid
        {
            Children =
            {
                new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x10)) },
                _number,
            },
        };
        RecordingChrome.Prepare(this, (w, h) => new PixelRect(area.Center.X - (w / 2), area.Center.Y - (h / 2), w, h));
    }

    /// <summary>Counts down; false when cancelled.</summary>
    public async Task<bool> RunAsync(int seconds, CancellationToken cancellationToken)
    {
        Show();
        try
        {
            for (var i = seconds; i > 0; i--)
            {
                _number.Text = i.ToString(System.Globalization.CultureInfo.CurrentCulture);
                await Task.Delay(1000, cancellationToken);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Close();
        }
    }
}

/// <summary>The floating bar while recording: time, pause, microphone mute, stop, discard. Drag it by its edge.</summary>
internal sealed class RecordingControlBar
{
    private readonly Window _window;
    private readonly TextBlock _time = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0), MinWidth = 52 };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x38, 0x3B)), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause;
    private readonly Button? _mute;
    private bool _moved;

    public RecordingControlBar(PixelRect area, PixelRect workArea, bool microphone)
    {
        _window = RecordingChrome.Panel("Grably recording");
        _window.ShowActivated = false;
        _time.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _pause = RecordingChrome.IconButton(SymbolRegular.Pause24, "Pause");
        _pause.Click += (_, _) => PauseClicked?.Invoke(this, EventArgs.Empty);
        var stop = RecordingChrome.IconButton(SymbolRegular.RecordStop24, "Stop and save", ControlAppearance.Primary);
        stop.Content = "Stop";
        stop.Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);
        var discard = RecordingChrome.IconButton(SymbolRegular.Delete24, "Discard this recording");
        discard.Click += (_, _) => DiscardClicked?.Invoke(this, EventArgs.Empty);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_time);
        row.Children.Add(_pause);
        if (microphone)
        {
            _mute = RecordingChrome.IconButton(SymbolRegular.Mic24, "Mute the microphone");
            _mute.Click += (_, _) => MuteClicked?.Invoke(this, EventArgs.Empty);
            row.Children.Add(_mute);
        }

        row.Children.Add(stop);
        row.Children.Add(discard);
        var card = RecordingChrome.Card(row);
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Border or StackPanel or TextBlock or Ellipse)
            {
                _window.DragMove();
                _moved = true;
            }
        };
        _window.Content = card;
        RecordingChrome.Prepare(_window, (w, h) => RecordingChrome.Beside(area, workArea, w, h), () => _moved);
    }

    public event EventHandler? PauseClicked;

    public event EventHandler? MuteClicked;

    public event EventHandler? StopClicked;

    public event EventHandler? DiscardClicked;

    /// <summary>The window itself (UI snapshots).</summary>
    internal Window Window => _window;

    public void Show() => _window.Show();

    public void Close() => _window.Close();

    public void Update(TimeSpan elapsed, bool paused, bool muted)
    {
        _time.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        _dot.Opacity = paused ? 0.3 : (DateTime.Now.Millisecond < 500 ? 1 : 0.6);
        _pause.Icon = new SymbolIcon { Symbol = paused ? SymbolRegular.Play24 : SymbolRegular.Pause24 };
        _pause.ToolTip = paused ? "Resume" : "Pause";
        if (_mute is not null)
        {
            _mute.Icon = new SymbolIcon { Symbol = muted ? SymbolRegular.MicOff24 : SymbolRegular.Mic24 };
            _mute.ToolTip = muted ? "Unmute the microphone" : "Mute the microphone";
        }
    }

    /// <summary>Ticks every quarter second on the UI thread while the bar is open.</summary>
    public DispatcherTimer Ticker(Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => tick();
        timer.Start();
        _window.Closed += (_, _) => timer.Stop();
        return timer;
    }
}
