using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Imaging;
using Grably.App.Shell;
using Grably.Core.Annotations;
using Grably.Core.Geometry;
using Grably.Core.Imaging;

namespace Grably.App.Editor;

/// <summary>
/// Draws the image and its annotations at the current zoom and turns pointer input into image-pixel positions for
/// <see cref="AnnotationEditor"/>. At zoom 1 one image pixel is one screen pixel, whatever the display scaling.
/// </summary>
public sealed class EditorCanvas : FrameworkElement
{
    private const double HandleSizeDip = 9;
    private const double PickToleranceDip = 5;

    private static readonly Brush CropDim = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Brush Mark = Frozen(new SolidColorBrush(ThemeController.Mark));

    private readonly AnnotationEditor _editor;
    private readonly BitmapSource _image;
    private readonly AnnotationRenderer _renderer;
    private double _zoom = 1;

    public EditorCanvas(AnnotationEditor editor, PixelBuffer image)
    {
        _editor = editor;
        _image = image.ToBitmapSource();
        _renderer = new AnnotationRenderer(image);
        Focusable = true;
        ClipToBounds = true;
        _editor.Changed += (_, _) =>
        {
            InvalidateMeasure(); // the view area changes with crop
            InvalidateVisual();
        };
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    /// <summary>1 = 100 %: one image pixel per screen pixel.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.05, 16);
            UpdateTolerances();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>DIPs per image pixel.</summary>
    public double Scale => _zoom / DpiScale;

    /// <summary>The image area currently shown: the whole image while cropping, otherwise the crop.</summary>
    public PixelRect View => _editor.Tool == EditorTool.Crop
        ? new PixelRect(0, 0, _editor.Document.Width, _editor.Document.Height)
        : _editor.Document.ViewArea;

    private double DpiScale => PresentationSource.FromVisual(this) is null ? 1 : VisualTreeHelper.GetDpi(this).DpiScaleX;

    public PointD ToImage(Point dip) => new((dip.X / Scale) + View.X, (dip.Y / Scale) + View.Y);

    public Point ToDip(PointD p) => new((p.X - View.X) * Scale, (p.Y - View.Y) * Scale);

    protected override Size MeasureOverride(Size availableSize) => new(View.Width * Scale, View.Height * Scale);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateTolerances();
        InvalidateMeasure();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var view = View;
        var s = Scale;
        dc.PushTransform(new ScaleTransform(s, s));
        dc.PushTransform(new TranslateTransform(-view.X, -view.Y));

        dc.DrawImage(_image, new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
        _renderer.ForgetRemoved(_editor.Document.Items);
        foreach (var item in _editor.Document.Items)
        {
            if (item != _editor.EditingText)
            {
                _renderer.Draw(dc, item, DpiScale * _zoom);
            }
        }

        if (_editor.Tool == EditorTool.Crop)
        {
            DrawCrop(dc, s);
        }
        else if (_editor.Selected is { } selected && selected != _editor.EditingText)
        {
            DrawSelection(dc, selected, s);
        }

        dc.Pop();
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = ToImage(e.GetPosition(this));
        if (e.ClickCount == 2)
        {
            _editor.DoubleClick(p);
            e.Handled = true;
            return;
        }

        CaptureMouse();
        _editor.Press(p, Modifiers());
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToImage(e.GetPosition(this));
        if (IsMouseCaptured)
        {
            _editor.Move(p, Modifiers());
        }

        Cursor = CursorFor(_editor.HoverHandle(p));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            // Finish the gesture at the release point first; releasing capture afterwards finds nothing left to end.
            _editor.Release(ToImage(e.GetPosition(this)), Modifiers());
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // Alt+Tab, a capture hotkey or a dialog took the mouse mid-drag: end the gesture where it got to, so nothing
        // is left half-drawn or moved without an undo step.
        if (_editor.IsGestureActive)
        {
            _editor.EndGesture();
        }
    }

    private void DrawSelection(DrawingContext dc, AnnotationItem item, double s)
    {
        var handles = AnnotationGeometry.Handles(item);
        if (handles.Count == 0 || item is LineItem)
        {
            // Items without box handles get a dashed outline so the selection is visible.
            var pen = new Pen(Mark, 1.5 / s) { DashStyle = new DashStyle([3, 3], 0) };
            if (item is not LineItem)
            {
                var b = item.Bounds.Inflate(3 / s);
                dc.DrawRectangle(null, pen, new Rect(b.X, b.Y, b.Width, b.Height));
            }
        }

        var size = HandleSizeDip / s;
        var outline = new Pen(Mark, 1.5 / s);
        foreach (var (_, p) in handles)
        {
            dc.DrawRectangle(Brushes.White, outline, new Rect(p.X - (size / 2), p.Y - (size / 2), size, size));
        }
    }

    private void DrawCrop(DrawingContext dc, double s)
    {
        var full = new Rect(0, 0, _editor.Document.Width, _editor.Document.Height);
        if (_editor.CropDraft is not { } crop)
        {
            return;
        }

        var hole = new Rect(crop.X, crop.Y, crop.Width, crop.Height);
        dc.DrawGeometry(CropDim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(hole)));
        dc.DrawRectangle(null, new Pen(Brushes.White, 2 / s), hole);

        var size = HandleSizeDip * 1.2 / s;
        var outline = new Pen(Mark, 1.5 / s);
        foreach (var (_, p) in AnnotationGeometry.Handles(new RectangleItem { Rect = new RectD(crop.X, crop.Y, crop.Width, crop.Height) }))
        {
            dc.DrawRectangle(Brushes.White, outline, new Rect(p.X - (size / 2), p.Y - (size / 2), size, size));
        }
    }

    private void UpdateTolerances()
    {
        var s = Scale;
        _editor.HitTolerance = PickToleranceDip / s;
        _editor.HandleRadius = HandleSizeDip / s;
    }

    private Cursor CursorFor(Handle handle) => handle switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Left or Handle.Right => Cursors.SizeWE,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        Handle.Start or Handle.End => Cursors.Hand,
        Handle.Body => Cursors.SizeAll,
        _ => _editor.Tool switch
        {
            EditorTool.Select => Cursors.Arrow,
            EditorTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        },
    };

    private static EditModifiers Modifiers()
    {
        var m = EditModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            m |= EditModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            m |= EditModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            m |= EditModifiers.Alt;
        }

        return m;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
