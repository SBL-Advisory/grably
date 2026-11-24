using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Grably.Core.Geometry;
using Grably.Platform.Display;

namespace Grably.App.Capture;

/// <summary>
/// The small panel shown during a scrolling capture: progress and a Stop button. It sits just outside the area
/// being captured and is excluded from captures, so it never ends up in the image.
/// </summary>
internal sealed class ScrollingCaptureWindow : Window
{
    private readonly PixelRect _area;
    private readonly PixelRect _workArea;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

    public ScrollingCaptureWindow(PixelRect area, PixelRect workArea)
    {
        _area = area;
        _workArea = workArea;
        Title = "Grably scrolling capture";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 0; // the theme's window style has a minimum size meant for app windows
        MinHeight = 0;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Update(0);

        var stop = new Wpf.Ui.Controls.Button { Content = "Stop", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 72 };
        stop.Click += (_, _) => StopRequested = true;
        var panel = new DockPanel();
        DockPanel.SetDock(stop, Dock.Right);
        panel.Children.Add(stop);
        panel.Children.Add(_status);
        var border = new Border { Padding = new Thickness(14, 10, 10, 10), BorderThickness = new Thickness(1), Child = panel };
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Content = border;

        SourceInitialized += (_, _) => WindowActions.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => Place();
    }

    /// <summary>The user clicked Stop.</summary>
    public bool StopRequested { get; private set; }

    public void Update(int height) => _status.Text = height == 0
        ? "Scrolling... Press Esc or click Stop to finish."
        : $"Scrolling... {height:N0} px so far. Press Esc or click Stop to finish.";

    /// <summary>Below the area if there is room on its monitor, else above it, else inside its bottom edge.</summary>
    private void Place()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var gap = (int)(8 * scale);
        var x = Math.Clamp(_area.X + ((_area.Width - width) / 2), _workArea.X, Math.Max(_workArea.X, _workArea.Right - width));
        var y = _area.Bottom + gap + height <= _workArea.Bottom ? _area.Bottom + gap
            : _area.Y - gap - height >= _workArea.Y ? _area.Y - gap - height
            : _area.Bottom - gap - height;
        ScreenPointer.PlaceTopmost(new WindowInteropHelper(this).Handle, new PixelRect(x, y, width, height));
    }
}
