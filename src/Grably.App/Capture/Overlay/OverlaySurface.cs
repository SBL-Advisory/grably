using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Imaging;
using Grably.App.Shell;
using Grably.Core.Capture;
using Grably.Core.Geometry;
using Grably.Core.Imaging;

namespace Grably.App.Capture.Overlay;

/// <summary>
/// Draws one monitor's part of the capture overlay: the frozen screen at one image pixel per screen pixel, the
/// dimming outside the selection (or hovered window), the selection frame and handles, the size label, a crosshair
/// and the magnifier loupe. Everything is computed in physical pixels and converted to this element's DIPs.
/// </summary>
public sealed class OverlaySurface : FrameworkElement
{
    private const int LoupeCells = 15;    // source pixels across
    private const int LoupeCellSize = 8;  // screen pixels per source pixel
    private const int LoupeOffset = 24;   // screen pixels from the pointer

    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)));
    private static readonly Brush LabelBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1A, 0x23)));
    private static readonly Brush Accent = Frozen(new SolidColorBrush(ThemeController.Accent));
    private static readonly Brush CrosshairBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GridBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));
    private static readonly Typeface LabelFont = new("Segoe UI");

    private readonly RegionSelection _selection;
    private readonly MonitorInfo _monitor;
    private readonly PixelBuffer _desktopImage;
    private readonly PixelRect _desktop;
    private readonly BitmapSource _monitorImage;
    private readonly Dictionary<int, Brush> _pixelBrushes = [];
    private RenderState? _rendered;

    /// <param name="desktopImage">The frozen virtual desktop; its pixel (0,0) is <paramref name="desktop"/>'s top-left.</param>
    public OverlaySurface(RegionSelection selection, MonitorInfo monitor, PixelBuffer desktopImage, PixelRect desktop)
    {
        _selection = selection;
        _monitor = monitor;
        _desktopImage = desktopImage;
        _desktop = desktop;
        _monitorImage = desktopImage.Crop(monitor.Bounds.Offset(-desktop.X, -desktop.Y)).ToBitmapSource(monitor.Dpi);

        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Focusable = true;
    }

    /// <summary>Screen pixels per DIP for this element: the window's DPI, or the monitor's when not in a window.</summary>
    private double Scale => PresentationSource.FromVisual(this) is null ? _monitor.Scale : VisualTreeHelper.GetDpi(this).DpiScaleX;

    protected override Size MeasureOverride(Size availableSize) =>
        new(_monitor.Bounds.Width / Scale, _monitor.Bounds.Height / Scale);

    /// <summary>
    /// True when the selection changed in a way that shows on this monitor. Pointer moves on other monitors only
    /// repaint those, which keeps dragging smooth on multi-monitor setups.
    /// </summary>
    public bool NeedsRedraw() => CurrentState() != _rendered;

    protected override void OnRender(DrawingContext dc)
    {
        _rendered = CurrentState();
        var s = Scale;
        var full = new Rect(0, 0, _monitor.Bounds.Width / s, _monitor.Bounds.Height / s);
        dc.DrawImage(_monitorImage, full);

        var highlight = _selection.HasSelection
            ? _selection.Selection
            : _selection.Phase is SelectionPhase.Pointing or SelectionPhase.Pressed ? _selection.HoverBounds : null;

        var visible = highlight is { } h ? h.Intersect(_monitor.Bounds) : PixelRect.Empty;
        if (visible.IsEmpty)
        {
            dc.DrawRectangle(Dim, null, full);
        }
        else
        {
            var hole = ToLocal(visible);
            dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(hole)));
        }

        if (highlight is { } area && area.IntersectsWith(_monitor.Bounds))
        {
            DrawFrame(dc, area, s);
            if (_selection.Phase is SelectionPhase.Adjusting or SelectionPhase.Moving or SelectionPhase.Resizing)
            {
                DrawHandles(dc, area, s);
            }

            if (_selection.HasSelection)
            {
                DrawSizeLabel(dc, area, s);
            }
        }

        if (_selection.Phase == SelectionPhase.Pointing)
        {
            DrawHint(dc, full, s);
        }

        if (_monitor.Bounds.Contains(_selection.Pointer) && _selection.Phase != SelectionPhase.Done)
        {
            DrawCrosshair(dc, full, s);
            DrawLoupe(dc, s);
        }
    }

    private Rect ToLocal(PixelRect r)
    {
        var s = Scale;
        return new Rect((r.X - _monitor.Bounds.X) / s, (r.Y - _monitor.Bounds.Y) / s, r.Width / s, r.Height / s);
    }

    private Point ToLocal(PixelPoint p)
    {
        var s = Scale;
        return new Point((p.X - _monitor.Bounds.X) / s, (p.Y - _monitor.Bounds.Y) / s);
    }

    private void DrawFrame(DrawingContext dc, PixelRect area, double s)
    {
        // Two screen pixels wide, drawn inside the area so it never covers what is outside.
        var pen = new Pen(Accent, 2 / s);
        var r = ToLocal(area);
        r.Inflate(-1 / s, -1 / s);
        dc.DrawRectangle(null, pen, r);
    }

    private void DrawHandles(DrawingContext dc, PixelRect area, double s)
    {
        var r = ToLocal(area);
        var size = 8 / s;
        var pen = new Pen(Accent, 1 / s);
        foreach (var x in new[] { r.Left, r.Left + (r.Width / 2), r.Right })
        {
            foreach (var y in new[] { r.Top, r.Top + (r.Height / 2), r.Bottom })
            {
                if (x == r.Left + (r.Width / 2) && y == r.Top + (r.Height / 2))
                {
                    continue; // no handle in the middle
                }

                dc.DrawRectangle(Brushes.White, pen, new Rect(x - (size / 2), y - (size / 2), size, size));
            }
        }
    }

    private void DrawSizeLabel(DrawingContext dc, PixelRect area, double s)
    {
        var text = Text($"{area.Width} × {area.Height}", s);
        var r = ToLocal(area);
        var padding = new Size(6 / s, 3 / s);
        var box = new Size(text.Width + (2 * padding.Width), text.Height + (2 * padding.Height));

        // Above the selection, or inside its top-left corner when there is no room above.
        var y = r.Top - box.Height - (4 / s);
        if (y < 0)
        {
            y = r.Top + (4 / s);
        }

        var x = Math.Clamp(r.Left, 0, Math.Max(0, ActualWidthOrMeasured().Width - box.Width));
        dc.DrawRoundedRectangle(LabelBackground, null, new Rect(new Point(x, y), box), 4 / s, 4 / s);
        dc.DrawText(text, new Point(x + padding.Width, y + padding.Height));
    }

    private void DrawHint(DrawingContext dc, Rect full, double s)
    {
        var text = Text("Click a window or drag to select an area. Esc cancels.", s);
        var padding = new Size(12 / s, 6 / s);
        var box = new Size(text.Width + (2 * padding.Width), text.Height + (2 * padding.Height));
        var origin = new Point((full.Width - box.Width) / 2, 24 / s);
        dc.DrawRoundedRectangle(LabelBackground, null, new Rect(origin, box), box.Height / 2, box.Height / 2);
        dc.DrawText(text, new Point(origin.X + padding.Width, origin.Y + padding.Height));
    }

    private void DrawCrosshair(DrawingContext dc, Rect full, double s)
    {
        var p = ToLocal(_selection.Pointer);
        var pen = new Pen(CrosshairBrush, 1 / s);
        dc.DrawLine(pen, new Point(0, p.Y), new Point(full.Width, p.Y));
        dc.DrawLine(pen, new Point(p.X, 0), new Point(p.X, full.Height));
    }

    private void DrawLoupe(DrawingContext dc, double s)
    {
        var pointer = _selection.Pointer;
        var cell = LoupeCellSize / s;
        var side = LoupeCells * cell;
        var info = Text($"{pointer.X}, {pointer.Y}   #{PixelAt(pointer):X6}", s);
        var infoHeight = info.Height + (8 / s);

        // Below-right of the pointer, flipped to stay on this monitor.
        var p = ToLocal(pointer);
        var size = ActualWidthOrMeasured();
        var x = p.X + (LoupeOffset / s);
        var y = p.Y + (LoupeOffset / s);
        if (x + side > size.Width)
        {
            x = p.X - (LoupeOffset / s) - side;
        }

        if (y + side + infoHeight > size.Height)
        {
            y = p.Y - (LoupeOffset / s) - side - infoHeight;
        }

        var frame = new Rect(x, y, side, side + infoHeight);
        dc.DrawRectangle(LabelBackground, new Pen(Brushes.White, 1 / s), frame);

        var half = LoupeCells / 2;
        for (var row = 0; row < LoupeCells; row++)
        {
            for (var col = 0; col < LoupeCells; col++)
            {
                var source = pointer.Offset(col - half, row - half);
                if (!_desktop.Contains(source))
                {
                    continue;
                }

                dc.DrawRectangle(BrushFor(PixelAt(source)), null, new Rect(x + (col * cell), y + (row * cell), cell, cell));
            }
        }

        var gridPen = new Pen(GridBrush, 1 / s);
        for (var i = 1; i < LoupeCells; i++)
        {
            dc.DrawLine(gridPen, new Point(x + (i * cell), y), new Point(x + (i * cell), y + side));
            dc.DrawLine(gridPen, new Point(x, y + (i * cell)), new Point(x + side, y + (i * cell)));
        }

        dc.DrawRectangle(null, new Pen(Accent, 2 / s), new Rect(x + (half * cell), y + (half * cell), cell, cell));
        dc.DrawText(info, new Point(x + ((side - info.Width) / 2), y + side + (4 / s)));
    }

    private int PixelAt(PixelPoint p) => _desktopImage.GetRgb(p.X - _desktop.X, p.Y - _desktop.Y);

    private Brush BrushFor(int rgb)
    {
        if (!_pixelBrushes.TryGetValue(rgb, out var brush))
        {
            if (_pixelBrushes.Count > 4096)
            {
                _pixelBrushes.Clear(); // screens can have millions of colours; keep the cache small
            }

            brush = Frozen(new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));
            _pixelBrushes[rgb] = brush;
        }

        return brush;
    }

    /// <summary>Everything that decides what this monitor shows; pointer details only matter when it is here.</summary>
    private RenderState CurrentState()
    {
        var pointerHere = _monitor.Bounds.Contains(_selection.Pointer);
        var highlight = _selection.HasSelection
            ? _selection.Selection
            : _selection.Phase is SelectionPhase.Pointing or SelectionPhase.Pressed ? _selection.HoverBounds : null;
        return new RenderState(
            _selection.Phase,
            highlight is { } h && h.IntersectsWith(_monitor.Bounds) ? h : null,
            pointerHere ? _selection.Pointer : null);
    }

    private sealed record RenderState(SelectionPhase Phase, PixelRect? Highlight, PixelPoint? Pointer);

    private Size ActualWidthOrMeasured() =>
        ActualWidth > 0 ? new Size(ActualWidth, ActualHeight) : MeasureOverride(Size.Empty);

    private static FormattedText Text(string text, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFont, 12, Brushes.White, pixelsPerDip);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
