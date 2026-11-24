using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Grably.App.Shell;
using Grably.Core.Recording;

namespace Grably.App.Trim;

/// <summary>
/// The trim editor's timeline, in the recording's own time: thumbnails and the sound wave, the removed parts shaded,
/// the selection, a handle at each end of what is kept, and the playhead. Click to move the playhead, drag to select,
/// drag a handle to trim (dragging it outwards brings a trimmed end back). Times are snapped to frame starts.
/// </summary>
public sealed class TrimTimeline : FrameworkElement
{
    private const double Pad = 12; // room for the handles at both ends
    private const double RulerHeight = 22;
    private const double HandleWidth = 12;
    private const double DragThreshold = 4;
    private const double TilesHigh = 0.62; // the thumbnails' share of the track, the rest is the sound wave

    private static readonly Color Mark = ThemeController.Mark;
    private static readonly Brush MarkBrush = Frozen(new SolidColorBrush(Mark));
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, Mark.R, Mark.G, Mark.B)));
    private static readonly Brush RemovedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x10)));
    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
    private static readonly Pen RemovedHatch = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen KeptPen = Frozen(new Pen(MarkBrush, 2));
    private static readonly Pen GripPen = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen PlayheadPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Pen PlayheadShadow = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0))), 4));

    private readonly List<BitmapSource> _tiles = [];
    private EditList? _edits;
    private TimeSpan _playhead;
    private TimeRange? _selection;
    private ImageSource? _waveform;
    private Drag _drag;
    private double _downX;
    private TimeSpan _downTime;
    private TimeSpan _handleTime;

    private enum Drag
    {
        None,
        Pending,
        Selecting,
        StartHandle,
        EndHandle,
    }

    public TrimTimeline()
    {
        Focusable = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>The playhead was moved with the mouse (a click, a selection drag or a handle drag).</summary>
    public event EventHandler<TimeSpan>? Scrubbed;

    /// <summary>The user selected a range, or cleared the selection with a click.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>A trim handle was let go: the new ends of the edited video.</summary>
    public event EventHandler<TimeRange>? EndsMoved;

    public double FrameRate { get; set; } = 30;

    public EditList? Edits
    {
        get => _edits;
        set
        {
            _edits = value;
            InvalidateVisual();
        }
    }

    public TimeSpan Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            InvalidateVisual();
        }
    }

    public TimeRange? Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            InvalidateVisual();
        }
    }

    public ImageSource? Waveform
    {
        get => _waveform;
        set
        {
            _waveform = value;
            InvalidateVisual();
        }
    }

    /// <summary>A strip of <paramref name="count"/> thumbnails side by side, evenly spread over the recording.</summary>
    public void SetThumbnails(BitmapSource? strip, int count)
    {
        _tiles.Clear();
        if (strip is not null && count > 0)
        {
            var width = strip.PixelWidth / count;
            for (var i = 0; i < count && width > 0; i++)
            {
                _tiles.Add(Frozen(new CroppedBitmap(strip, new Int32Rect(i * width, 0, width, strip.PixelHeight))));
            }
        }

        InvalidateVisual();
    }

    private TimeSpan Length => _edits?.SourceDuration is { Ticks: > 0 } d ? d : TimeSpan.FromSeconds(1);

    private double TrackWidth => Math.Max(1, ActualWidth - (2 * Pad));

    private double X(TimeSpan time) => Pad + (TrackWidth * Math.Clamp(time / Length, 0, 1));

    private TimeSpan TimeAt(double x)
    {
        var time = Length * Math.Clamp((x - Pad) / TrackWidth, 0, 1);
        return time >= Length ? Length : VideoFrames.Snap(time, FrameRate);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, 132);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height)); // hit-testable everywhere
        var track = new Rect(Pad, RulerHeight, TrackWidth, Math.Max(10, height - RulerHeight - 2));
        DrawRuler(dc, track);

        dc.PushClip(new RectangleGeometry(track, 6, 6));
        dc.DrawRectangle(TrackBrush, null, track);
        var tiles = track with { Height = Math.Round(track.Height * TilesHigh) };
        DrawTiles(dc, tiles);
        if (_waveform is not null)
        {
            dc.DrawImage(_waveform, new Rect(track.Left, tiles.Bottom, track.Width, track.Bottom - tiles.Bottom));
        }

        if (_edits is { } edits)
        {
            var ends = DraggedEnds(edits);
            DrawRemoved(dc, track, edits, ends);
        }

        if (_selection is { } selection)
        {
            var area = new Rect(new Point(X(selection.Start), track.Top), new Point(X(selection.End), track.Bottom));
            dc.DrawRectangle(SelectionBrush, null, area);
            dc.DrawRectangle(null, KeptPen, area);
        }

        dc.Pop();

        if (_edits is { } list)
        {
            var (start, end) = DraggedEnds(list);
            DrawHandle(dc, track, X(start), left: true);
            DrawHandle(dc, track, X(end), left: false);
        }

        var x = Math.Round(X(_playhead)) + 0.5;
        dc.DrawLine(PlayheadShadow, new Point(x, RulerHeight - 6), new Point(x, track.Bottom));
        dc.DrawLine(PlayheadPen, new Point(x, RulerHeight - 6), new Point(x, track.Bottom));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(x - 6, RulerHeight - 12), true, true);
            g.LineTo(new Point(x + 6, RulerHeight - 12), true, false);
            g.LineTo(new Point(x, RulerHeight - 4), true, false);
        }

        head.Freeze();
        dc.DrawGeometry(Brushes.White, PlayheadShadow, head);
        dc.DrawGeometry(Brushes.White, null, head);
    }

    /// <summary>The ends as they would be with the handle being dragged where it is now.</summary>
    private (TimeSpan Start, TimeSpan End) DraggedEnds(EditList edits) => _drag switch
    {
        Drag.StartHandle => (_handleTime, edits.End),
        Drag.EndHandle => (edits.Start, _handleTime),
        _ => (edits.Start, edits.End),
    };

    private void DrawRuler(DrawingContext dc, Rect track)
    {
        var text = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        var tickPen = new Pen(text, 1);
        tickPen.Freeze();
        var seconds = Length.TotalSeconds;
        var step = new[] { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 }
            .FirstOrDefault(s => track.Width / (seconds / s) >= 70, 7200);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var t = 0.0; t <= seconds + 1e-9; t += step)
        {
            var x = Math.Round(X(TimeSpan.FromSeconds(t))) + 0.5;
            dc.DrawLine(tickPen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight - 2));
            var label = new FormattedText(
                Label(TimeSpan.FromSeconds(t), step), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, text, dpi);
            var left = Math.Clamp(x - (label.Width / 2), 0, Math.Max(0, ActualWidth - label.Width));
            dc.DrawText(label, new Point(left, 0));
        }
    }

    private static string Label(TimeSpan time, double step) =>
        step < 1 ? VideoFrames.Format(time)[..^1] : time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Thumbnails at their own shape, repeated across the track, each showing the moment under its middle.</summary>
    private void DrawTiles(DrawingContext dc, Rect area)
    {
        if (_tiles.Count == 0)
        {
            return;
        }

        var tileWidth = area.Height * _tiles[0].PixelWidth / Math.Max(1, _tiles[0].PixelHeight);
        for (var left = area.Left; left < area.Right; left += tileWidth)
        {
            var middle = Math.Clamp((left + (tileWidth / 2) - area.Left) / area.Width, 0, 0.9999);
            dc.DrawImage(_tiles[(int)(middle * _tiles.Count)], new Rect(left, area.Top, tileWidth, area.Height));
        }
    }

    private void DrawRemoved(DrawingContext dc, Rect track, EditList edits, (TimeSpan Start, TimeSpan End) ends)
    {
        // What is kept between the (possibly dragged) ends; everything else is shaded and hatched.
        var kept = edits.WithEnds(ends.Start, ends.End).Kept;
        var removed = new List<(TimeSpan, TimeSpan)>();
        var at = TimeSpan.Zero;
        foreach (var part in kept)
        {
            removed.Add((at, part.Start));
            at = part.End;
        }

        removed.Add((at, Length));
        foreach (var (from, to) in removed.Where(r => r.Item2 > r.Item1))
        {
            var area = new Rect(new Point(X(from), track.Top), new Point(X(to), track.Bottom));
            dc.DrawRectangle(RemovedBrush, null, area);
            dc.PushClip(new RectangleGeometry(area));
            for (var x = area.Left - track.Height; x < area.Right; x += 9)
            {
                dc.DrawLine(RemovedHatch, new Point(x, track.Bottom), new Point(x + track.Height, track.Top));
            }

            dc.Pop();
        }

        foreach (var part in kept)
        {
            dc.DrawLine(KeptPen, new Point(X(part.Start), track.Top + 1), new Point(X(part.End), track.Top + 1));
            dc.DrawLine(KeptPen, new Point(X(part.Start), track.Bottom - 1), new Point(X(part.End), track.Bottom - 1));
        }
    }

    private static void DrawHandle(DrawingContext dc, Rect track, double x, bool left)
    {
        var area = new Rect(left ? x - HandleWidth : x, track.Top, HandleWidth, track.Height);
        dc.DrawRoundedRectangle(MarkBrush, null, area, 4, 4);
        var middle = area.Left + (HandleWidth / 2);
        var top = track.Top + (track.Height / 2) - 8;
        dc.DrawLine(GripPen, new Point(middle - 2, top), new Point(middle - 2, top + 16));
        dc.DrawLine(GripPen, new Point(middle + 2, top), new Point(middle + 2, top + 16));
    }

    private Drag HandleAt(double x)
    {
        if (_edits is not { Kept.Count: > 0 } edits)
        {
            return Drag.None;
        }

        var start = X(edits.Start);
        var end = X(edits.End);
        if (x >= start - HandleWidth - 2 && x <= start + 3)
        {
            return Drag.StartHandle;
        }

        return x >= end - 3 && x <= end + HandleWidth + 2 ? Drag.EndHandle : Drag.None;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_edits is null)
        {
            return;
        }

        var x = e.GetPosition(this).X;
        _drag = HandleAt(x);
        if (_drag == Drag.None)
        {
            _drag = Drag.Pending;
            _downX = x;
            _downTime = TimeAt(x);
            Scrubbed?.Invoke(this, _downTime);
        }
        else
        {
            _handleTime = _drag == Drag.StartHandle ? _edits.Start : _edits.End;
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;
        if (!IsMouseCaptured || _edits is not { } edits)
        {
            Cursor = HandleAt(x) == Drag.None ? Cursors.Arrow : Cursors.SizeWE;
            return;
        }

        var time = TimeAt(x);
        switch (_drag)
        {
            case Drag.Pending when Math.Abs(x - _downX) >= DragThreshold:
                _drag = Drag.Selecting;
                goto case Drag.Selecting;
            case Drag.Selecting:
                _selection = time < _downTime ? new TimeRange(time, _downTime) : new TimeRange(_downTime, time);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                Scrubbed?.Invoke(this, time);
                break;
            case Drag.StartHandle:
                // At least one frame stays, and the handle stays on a frame start.
                var lastStart = VideoFrames.Start(Math.Max(0, VideoFrames.Index(edits.End, FrameRate) - 1), FrameRate);
                _handleTime = time < lastStart ? time : lastStart;
                Scrubbed?.Invoke(this, _handleTime);
                break;
            case Drag.EndHandle:
                var firstEnd = VideoFrames.Start(VideoFrames.Index(edits.Start, FrameRate) + 1, FrameRate);
                _handleTime = time > firstEnd ? time : firstEnd;
                Scrubbed?.Invoke(this, _handleTime >= edits.SourceDuration ? _handleTime - TimeSpan.FromSeconds(1 / FrameRate) : _handleTime);
                break;
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
        {
            return;
        }

        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        switch (drag)
        {
            case Drag.Pending when _selection is not null:
                _selection = null;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case Drag.StartHandle when _edits is { } edits:
                EndsMoved?.Invoke(this, new TimeRange(_handleTime, edits.End));
                break;
            case Drag.EndHandle when _edits is { } edits:
                EndsMoved?.Invoke(this, new TimeRange(edits.Start, _handleTime));
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is Drag.StartHandle or Drag.EndHandle)
        {
            _drag = Drag.None; // Escape or another window took the mouse: the handle goes back
            InvalidateVisual();
        }
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
