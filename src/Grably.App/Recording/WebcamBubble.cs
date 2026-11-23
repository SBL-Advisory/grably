using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Grably.Core.Geometry;
using Grably.Core.Settings;
using Grably.Platform.Display;
using Grably.Platform.Recording;

namespace Grably.App.Recording;

/// <summary>
/// The camera picture on screen during a recording: a mirrored circle or rounded rectangle the user can drag. It is
/// excluded from capture; the compositor draws the same picture into the video wherever the bubble is, read through
/// <see cref="ScreenRect"/>.
/// </summary>
internal sealed class WebcamBubble : Window
{
    private readonly IWebcamFrames _camera;
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private long _shownVersion = -1;
    private volatile System.Runtime.CompilerServices.StrongBox<PixelRect>? _screenRect; // swapped whole: safe to read anywhere

    /// <param name="area">The recorded area; the bubble starts in its bottom-right corner.</param>
    public WebcamBubble(IWebcamFrames camera, PixelRect area, WebcamShape shape, WebcamSize size)
    {
        _camera = camera;
        Title = "Grably camera";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        MinWidth = 0;
        MinHeight = 0;
        Cursor = Cursors.SizeAll;
        var side = size switch { WebcamSize.Small => 140.0, WebcamSize.Large => 280.0, _ => 200.0 };
        Width = shape == WebcamShape.Circle ? side : side * 4 / 3;
        Height = side;
        _image.RenderTransform = new ScaleTransform(-1, 1); // a mirror, as people expect to see themselves

        Geometry clip = shape == WebcamShape.Circle
            ? new EllipseGeometry(new Rect(0, 0, Width, Height))
            : new RectangleGeometry(new Rect(0, 0, Width, Height), side * 0.12, side * 0.12);
        var picture = new Grid { Clip = clip, Background = Brushes.Black };
        picture.Children.Add(_image);
        var edge = shape == WebcamShape.Circle
            ? (Shape)new Ellipse { Stroke = Brushes.White, StrokeThickness = 3, Opacity = 0.85 }
            : new Rectangle { Stroke = Brushes.White, StrokeThickness = 3, Opacity = 0.85, RadiusX = side * 0.12, RadiusY = side * 0.12 };
        Content = new Grid { Children = { picture, edge } };

        MouseLeftButtonDown += (_, _) => DragMove();
        LocationChanged += (_, _) => UpdateScreenRect();
        SizeChanged += (_, _) => UpdateScreenRect(); // a DPI change resizes without moving
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            WindowActions.ExcludeFromCapture(handle);
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var w = (int)Math.Round(Width * scale);
            var h = (int)Math.Round(Height * scale);
            var margin = (int)(24 * scale);
            ScreenPointer.PlaceTopmost(handle, new PixelRect(area.Right - w - margin, area.Bottom - h - margin, w, h));
            UpdateScreenRect();
        };

        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => ShowLatest();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Where the bubble is on screen in physical pixels, for the compositor (any thread).</summary>
    public PixelRect? ScreenRect => _screenRect?.Value;

    private void UpdateScreenRect()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0 && WindowActions.Bounds(handle) is { } bounds)
        {
            _screenRect = new System.Runtime.CompilerServices.StrongBox<PixelRect>(bounds);
        }
    }

    private void ShowLatest()
    {
        if (_camera.Latest is not { } frame || frame.Version == _shownVersion)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
        _shownVersion = frame.Version;
    }
}
