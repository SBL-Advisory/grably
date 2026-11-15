using Grably.Core.Geometry;

namespace Grably.Core.Capture;

public enum SelectionPhase
{
    /// <summary>Nothing pressed: hovering highlights the window under the pointer.</summary>
    Pointing,

    /// <summary>Button down but not moved far enough to count as a drag yet (a click selects the hovered window).</summary>
    Pressed,

    Dragging,

    /// <summary>A selection exists and can be moved, resized or nudged before confirming (adjust mode).</summary>
    Adjusting,

    Moving,
    Resizing,

    /// <summary>Finished: <see cref="RegionSelection.Result"/> holds the area, or null when cancelled.</summary>
    Done,
}

[Flags]
public enum ResizeEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>
/// What the capture overlay does with the pointer and keyboard, independent of any UI so it can be unit-tested.
/// All coordinates are physical pixels of the virtual desktop. Not thread-safe: drive it from the UI thread.
/// </summary>
public sealed class RegionSelection
{
    /// <summary>How far (in pixels) the pointer must move with the button down before it counts as a drag.</summary>
    public const int DragThreshold = 4;

    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private PixelPoint _pressPoint;
    private PixelRect _pressSelection;
    private ResizeEdges _resizeEdges;

    /// <param name="desktop">The virtual desktop; selections never leave it.</param>
    /// <param name="monitors">Monitors, for clicks where no window is under the pointer.</param>
    /// <param name="windowsTopToBottom">Snap targets in z-order, topmost first, excluding Grably's own windows.</param>
    /// <param name="adjustBeforeCapture">Keep the selection editable after the drag instead of capturing on release.</param>
    public RegionSelection(PixelRect desktop, IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<WindowInfo> windowsTopToBottom, bool adjustBeforeCapture)
    {
        Desktop = desktop;
        _monitors = monitors;
        _windows = windowsTopToBottom;
        AdjustBeforeCapture = adjustBeforeCapture;
    }

    public event EventHandler? Changed;

    public event EventHandler? Completed;

    public PixelRect Desktop { get; }

    public bool AdjustBeforeCapture { get; }

    public SelectionPhase Phase { get; private set; } = SelectionPhase.Pointing;

    public PixelPoint Pointer { get; private set; }

    /// <summary>The window under the pointer while pointing; the highlight shown before anything is selected.</summary>
    public WindowInfo? HoverWindow { get; private set; }

    public PixelRect? HoverBounds => HoverWindow is null ? null : NonEmpty(WindowHitTest.VisibleBounds(HoverWindow, Desktop));

    /// <summary>The area being dragged or adjusted.</summary>
    public PixelRect? Selection { get; private set; }

    /// <summary>When <see cref="Phase"/> is Done: the captured area, or null if the user cancelled.</summary>
    public PixelRect? Result { get; private set; }

    /// <summary>The window the result came from (clicked, or under the selection's centre), for the library.</summary>
    public WindowInfo? SourceWindow { get; private set; }

    /// <summary>True while a selection is visible (dragging or adjusting).</summary>
    public bool HasSelection => Selection is not null && Phase is SelectionPhase.Dragging or SelectionPhase.Adjusting
        or SelectionPhase.Moving or SelectionPhase.Resizing;

    public void MovePointer(PixelPoint p)
    {
        if (Phase == SelectionPhase.Done)
        {
            return;
        }

        Pointer = Desktop.Clamp(p);
        switch (Phase)
        {
            case SelectionPhase.Pointing:
                HoverWindow = WindowHitTest.WindowAt(_windows, Pointer);
                break;
            case SelectionPhase.Pressed when Distance(_pressPoint, Pointer) >= DragThreshold:
                Phase = SelectionPhase.Dragging;
                Selection = PixelRect.FromCorners(_pressPoint, Pointer);
                HoverWindow = null;
                break;
            case SelectionPhase.Dragging:
                Selection = PixelRect.FromCorners(_pressPoint, Pointer);
                break;
            case SelectionPhase.Moving:
                Selection = _pressSelection.Offset(Pointer.X - _pressPoint.X, Pointer.Y - _pressPoint.Y).MoveInside(Desktop);
                break;
            case SelectionPhase.Resizing:
                Selection = Resize(_pressSelection, _resizeEdges, Pointer);
                break;
        }

        OnChanged();
    }

    public void Press(PixelPoint p)
    {
        if (Phase == SelectionPhase.Done)
        {
            return;
        }

        Pointer = Desktop.Clamp(p);
        _pressPoint = Pointer;
        if (Phase == SelectionPhase.Adjusting && Selection is { } current)
        {
            _pressSelection = current;
            _resizeEdges = HitHandle(Pointer);
            if (_resizeEdges != ResizeEdges.None)
            {
                Phase = SelectionPhase.Resizing;
                SourceWindow = null; // recomputed from the final area
                OnChanged();
                return;
            }

            if (current.Contains(Pointer))
            {
                Phase = SelectionPhase.Moving;
                SourceWindow = null;
                OnChanged();
                return;
            }

            // Pressing outside the selection starts over.
            Selection = null;
            HoverWindow = WindowHitTest.WindowAt(_windows, Pointer);
        }

        SourceWindow = null;
        Phase = SelectionPhase.Pressed;
        OnChanged();
    }

    public void Release(PixelPoint p)
    {
        if (Phase == SelectionPhase.Done)
        {
            return;
        }

        MovePointer(p);
        switch (Phase)
        {
            case SelectionPhase.Pressed:
                // A click: take the window under the pointer, or the whole monitor when there is none.
                var target = HoverBounds ?? MonitorAt(Pointer)?.Bounds ?? Desktop;
                SourceWindow = HoverWindow;
                if (AdjustBeforeCapture)
                {
                    Selection = target;
                    Phase = SelectionPhase.Adjusting;
                    OnChanged();
                }
                else
                {
                    Complete(target);
                }

                break;
            case SelectionPhase.Dragging when AdjustBeforeCapture:
            case SelectionPhase.Moving:
            case SelectionPhase.Resizing:
                Phase = SelectionPhase.Adjusting;
                OnChanged();
                break;
            case SelectionPhase.Dragging:
                Complete(Selection!.Value);
                break;
        }
    }

    /// <summary>Enter or double-click: capture the current selection (adjust mode).</summary>
    public void Confirm()
    {
        if (Phase == SelectionPhase.Adjusting && Selection is { } selection)
        {
            Complete(selection);
        }
    }

    /// <summary>Esc or right-click: end without capturing.</summary>
    public void Cancel()
    {
        if (Phase == SelectionPhase.Done)
        {
            return;
        }

        Phase = SelectionPhase.Done;
        Result = null;
        Selection = null;
        OnChanged();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Arrow keys in adjust mode: move the selection, or grow/shrink it from the bottom-right corner.</summary>
    public void Nudge(int dx, int dy, bool resize)
    {
        if (Phase != SelectionPhase.Adjusting || Selection is not { } s)
        {
            return;
        }

        Selection = resize
            ? (s with { Width = Math.Max(1, s.Width + dx), Height = Math.Max(1, s.Height + dy) }).Intersect(Desktop)
            : s.Offset(dx, dy).MoveInside(Desktop);
        SourceWindow = null;
        OnChanged();
    }

    /// <summary>Which edges a press at <paramref name="p"/> would drag: the selection's corner and edge handles.</summary>
    public ResizeEdges HitHandle(PixelPoint p, int tolerance = 6)
    {
        if (Selection is not { } s || Phase is not (SelectionPhase.Adjusting or SelectionPhase.Resizing))
        {
            return ResizeEdges.None;
        }

        var edges = ResizeEdges.None;
        var (nearLeft, nearRight) = NearEdges(p.X, s.X, s.Right - 1, tolerance);
        var (nearTop, nearBottom) = NearEdges(p.Y, s.Y, s.Bottom - 1, tolerance);
        var withinX = p.X >= s.X - tolerance && p.X <= s.Right - 1 + tolerance;
        var withinY = p.Y >= s.Y - tolerance && p.Y <= s.Bottom - 1 + tolerance;
        var midX = Math.Abs(p.X - s.Center.X) <= tolerance;
        var midY = Math.Abs(p.Y - s.Center.Y) <= tolerance;

        // Corners, then the middle of each side (the 8 handles drawn by the overlay).
        if (nearLeft && withinY && (nearTop || nearBottom || midY))
        {
            edges |= ResizeEdges.Left;
        }

        if (nearRight && withinY && (nearTop || nearBottom || midY))
        {
            edges |= ResizeEdges.Right;
        }

        if (nearTop && withinX && (nearLeft || nearRight || midX))
        {
            edges |= ResizeEdges.Top;
        }

        if (nearBottom && withinX && (nearLeft || nearRight || midX))
        {
            edges |= ResizeEdges.Bottom;
        }

        return edges;
    }

    public MonitorInfo? MonitorAt(PixelPoint p) => _monitors.FirstOrDefault(m => m.Bounds.Contains(p));

    /// <summary>
    /// Whether <paramref name="v"/> is near the low or high edge along one axis. On a selection narrower than two
    /// tolerances both would match; then only a press on or beyond an edge pixel counts, so a press inside a small
    /// selection moves it instead of collapsing it.
    /// </summary>
    private static (bool Low, bool High) NearEdges(int v, int low, int high, int tolerance)
    {
        var nearLow = Math.Abs(v - low) <= tolerance;
        var nearHigh = Math.Abs(v - high) <= tolerance;
        if (nearLow && nearHigh)
        {
            return (v <= low, v >= high && v > low);
        }

        return (nearLow, nearHigh);
    }

    private void Complete(PixelRect area)
    {
        var clipped = area.Intersect(Desktop);
        Phase = SelectionPhase.Done;
        Result = clipped.IsEmpty ? null : clipped;
        Selection = Result;
        SourceWindow ??= Result is { } r ? WindowHitTest.WindowAt(_windows, r.Center) : null;
        OnChanged();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private PixelRect Resize(PixelRect from, ResizeEdges edges, PixelPoint p)
    {
        var left = from.X;
        var top = from.Y;
        var right = from.Right;
        var bottom = from.Bottom;
        if (edges.HasFlag(ResizeEdges.Left))
        {
            left = Math.Min(p.X, right - 1);
        }

        if (edges.HasFlag(ResizeEdges.Right))
        {
            right = Math.Max(p.X + 1, left + 1);
        }

        if (edges.HasFlag(ResizeEdges.Top))
        {
            top = Math.Min(p.Y, bottom - 1);
        }

        if (edges.HasFlag(ResizeEdges.Bottom))
        {
            bottom = Math.Max(p.Y + 1, top + 1);
        }

        return PixelRect.FromEdges(left, top, right, bottom).Intersect(Desktop);
    }

    private static int Distance(PixelPoint a, PixelPoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static PixelRect? NonEmpty(PixelRect r) => r.IsEmpty ? null : r;

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
