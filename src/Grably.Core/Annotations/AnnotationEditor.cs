using Grably.Core.Geometry;

namespace Grably.Core.Annotations;

public enum EditorTool
{
    Select,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Pen,
    Highlighter,
    Text,
    Step,
    Blur,
    Pixelate,
    Crop,
}

[Flags]
public enum EditModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>
/// The editor's behaviour, independent of WPF: what each tool does with the pointer, selection, handles, z-order,
/// clipboard of items, styles and undo. The editor window draws <see cref="Document"/> and forwards input here.
/// All positions are image pixels of the original image. Drive it from the UI thread.
/// </summary>
public sealed class AnnotationEditor
{
    /// <summary>Drags shorter than this create nothing (a stray click with a shape tool).</summary>
    public const double MinimumSize = 3;

    private readonly Dictionary<EditorTool, AnnotationStyle> _toolStyles;
    private Gesture? _gesture;
    private EditorTool _tool = EditorTool.Arrow;

    public AnnotationEditor(AnnotationDocument document, IReadOnlyDictionary<EditorTool, AnnotationStyle>? toolStyles = null)
    {
        Document = document;
        History = new UndoHistory(document);
        _toolStyles = DefaultToolStyles();
        foreach (var (tool, style) in toolStyles ?? new Dictionary<EditorTool, AnnotationStyle>())
        {
            _toolStyles[tool] = style;
        }
    }

    /// <summary>Raised whenever anything visible changed (redraw).</summary>
    public event EventHandler? Changed;

    /// <summary>A text item needs its in-place text box (new text, or a double-click on existing text).</summary>
    public event EventHandler<TextItem>? TextEditRequested;

    public AnnotationDocument Document { get; private set; }

    public UndoHistory History { get; }

    public AnnotationItem? Selected { get; private set; }

    /// <summary>The crop being drawn or adjusted while the crop tool is active.</summary>
    public PixelRect? CropDraft { get; private set; }

    /// <summary>The text item whose text box is open; the canvas does not draw it meanwhile.</summary>
    public TextItem? EditingText { get; private set; }

    /// <summary>Pick distance around strokes, in image pixels; the window sets it from the zoom.</summary>
    public double HitTolerance { get; set; } = 4;

    /// <summary>Half the size of a handle square, in image pixels; the window sets it from the zoom.</summary>
    public double HandleRadius { get; set; } = 6;

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
            {
                return;
            }

            FinishTextEdit();
            _tool = value;
            CropDraft = value == EditorTool.Crop ? Document.ViewArea : null;
            if (value != EditorTool.Select && value != EditorTool.Crop && Selected is not null && ToolFor(Selected) != value)
            {
                Selected = null;
            }

            OnChanged();
        }
    }

    /// <summary>The style the property bar shows: the selected item's, or the current tool's for new items.</summary>
    public AnnotationStyle CurrentStyle => Selected?.Style ?? _toolStyles[_tool == EditorTool.Select ? EditorTool.Arrow : _tool];

    /// <summary>The remembered style of every tool, to persist between sessions.</summary>
    public IReadOnlyDictionary<EditorTool, AnnotationStyle> ToolStyles => _toolStyles;

    public static Dictionary<EditorTool, AnnotationStyle> DefaultToolStyles()
    {
        var shape = new AnnotationStyle();
        return new Dictionary<EditorTool, AnnotationStyle>
        {
            [EditorTool.Arrow] = shape,
            [EditorTool.Line] = shape,
            [EditorTool.Rectangle] = shape,
            [EditorTool.Ellipse] = shape,
            [EditorTool.Pen] = shape with { Shadow = false },
            [EditorTool.Highlighter] = new AnnotationStyle { Stroke = new Rgba(0xFF, 0xE6, 0x00), Thickness = 18, Opacity = 0.45, Shadow = false },
            [EditorTool.Text] = shape with { Shadow = false },
            [EditorTool.Step] = shape,
            [EditorTool.Blur] = shape with { Shadow = false },
            [EditorTool.Pixelate] = shape with { Shadow = false },
            [EditorTool.Crop] = shape,
        };
    }

    /// <summary>Replaces the document (open, undo, redo) and drops any selection that no longer exists.</summary>
    public void Load(AnnotationDocument document, bool resetHistory = false)
    {
        var selectedId = Selected?.Id;
        Document = document;
        Selected = selectedId is null ? null : document.Find(selectedId);
        EditingText = null;
        _gesture = null;
        if (_tool == EditorTool.Crop)
        {
            CropDraft = document.ViewArea;
        }

        if (resetHistory)
        {
            History.Reset(document);
        }

        OnChanged();
    }

    // ------------------------------------------------------------------------------------------------- pointer

    public void Press(PointD p, EditModifiers modifiers = EditModifiers.None)
    {
        FinishTextEdit();
        _lastPointer = p;
        switch (_tool)
        {
            case EditorTool.Crop:
                PressCrop(p);
                break;

            case EditorTool.Text when AnnotationGeometry.TopmostAt(Document.Items, p, HitTolerance) is TextItem existing:
                Selected = existing;
                BeginTextEdit(existing);
                break;

            case EditorTool.Select:
            case var _ when Selected is not null && AnnotationGeometry.HandleAt(Selected, p, HandleRadius, HitTolerance) is not Handle.None and not Handle.Body:
                PressSelect(p);
                break;

            default:
                Create(p);
                break;
        }

        OnChanged();
    }

    public void Move(PointD p, EditModifiers modifiers = EditModifiers.None)
    {
        if (_gesture is null)
        {
            return;
        }

        _lastPointer = p;
        var constrain = modifiers.HasFlag(EditModifiers.Shift);
        var g = _gesture;
        switch (g.Kind)
        {
            case GestureKind.Drag:
                AnnotationGeometry.Drag(g.Item!, g.Original!, g.Handle, g.Start, p, constrain);
                break;

            case GestureKind.CreateLine:
                var line = (LineItem)g.Item!;
                line.End = constrain ? AnnotationGeometry.SnapAngle(line.Start, p) : p;
                break;

            case GestureKind.CreateRect:
                ((IRectItem)g.Item!).Rect = constrain ? AnnotationGeometry.Square(g.Start, p) : RectD.FromPoints(g.Start, p);
                break;

            case GestureKind.CreateStroke:
                var stroke = (StrokeItem)g.Item!;
                if (constrain)
                {
                    stroke.Points = [stroke.Points[0], p]; // Shift: a straight stroke, e.g. underlining with the highlighter
                }
                else if (stroke.Points[^1].DistanceTo(p) >= 1)
                {
                    stroke.Points.Add(p);
                }

                break;

            case GestureKind.CropNew:
                CropDraft = ToPixelRect(constrain ? AnnotationGeometry.Square(g.Start, p) : RectD.FromPoints(g.Start, p));
                break;

            case GestureKind.CropDrag:
                CropDraft = DragCrop(g, p, constrain);
                break;
        }

        OnChanged();
    }

    public void Release(PointD p, EditModifiers modifiers = EditModifiers.None)
    {
        if (_gesture is null)
        {
            return;
        }

        Move(p, modifiers);
        var g = _gesture;
        _gesture = null;
        switch (g.Kind)
        {
            case GestureKind.Drag:
                Commit();
                break;

            case GestureKind.CreateLine or GestureKind.CreateRect:
                var tooSmall = g.Item switch
                {
                    LineItem line => line.Start.DistanceTo(line.End) < MinimumSize,
                    IRectItem { Rect: var r } => r.Width < MinimumSize || r.Height < MinimumSize,
                    _ => false,
                };
                if (tooSmall)
                {
                    Remove(g.Item!); // a click, not a shape
                }
                else
                {
                    Commit();
                }

                break;

            case GestureKind.CreateStroke:
                var stroke = (StrokeItem)g.Item!;
                if (stroke.Kind == StrokeKind.Highlighter && stroke.Points.Count < 2)
                {
                    Remove(stroke);
                }
                else
                {
                    Commit();
                }

                break;

            case GestureKind.CropNew or GestureKind.CropDrag:
                ApplyCropDraft();
                break;
        }

        OnChanged();
    }

    /// <summary>Double-click: edit text in place.</summary>
    public void DoubleClick(PointD p)
    {
        if (AnnotationGeometry.TopmostAt(Document.Items, p, HitTolerance) is TextItem text)
        {
            Selected = text;
            BeginTextEdit(text);
            OnChanged();
        }
    }

    /// <summary>What the pointer is over, for the cursor shape.</summary>
    public Handle HoverHandle(PointD p)
    {
        if (_tool == EditorTool.Crop)
        {
            return CropDraft is { } crop ? CropHandleAt(crop, p) : Handle.None;
        }

        if (Selected is not null)
        {
            var h = AnnotationGeometry.HandleAt(Selected, p, HandleRadius, HitTolerance);
            if (h != Handle.None && (_tool == EditorTool.Select || h != Handle.Body))
            {
                return h;
            }
        }

        return _tool == EditorTool.Select && AnnotationGeometry.TopmostAt(Document.Items, p, HitTolerance) is not null
            ? Handle.Body
            : Handle.None;
    }

    // ------------------------------------------------------------------------------------------------- text

    /// <summary>The text box closed: store the text, or remove the item if it is empty.</summary>
    public void SetText(TextItem item, string text)
    {
        if (EditingText == item)
        {
            EditingText = null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Remove(item); // a new text that was never committed leaves nothing behind; an emptied one is a deletion
        }
        else
        {
            item.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        Commit();
        OnChanged();
    }

    /// <summary>True while a pointer gesture (drawing, dragging, cropping) is in progress.</summary>
    public bool IsGestureActive => _gesture is not null;

    /// <summary>
    /// Ends a gesture the pointer can no longer finish (mouse capture lost to Alt+Tab or a dialog) at the last
    /// position it reached, so nothing is left half-drawn or uncommitted.
    /// </summary>
    public void EndGesture()
    {
        if (_gesture is { } g)
        {
            Release(_lastPointer ?? g.Start);
        }
    }

    private PointD? _lastPointer;

    private void BeginTextEdit(TextItem item)
    {
        EditingText = item;
        TextEditRequested?.Invoke(this, item);
    }

    /// <summary>Called when another action starts while a text box is open; the window commits the box's text.</summary>
    public event EventHandler? TextEditEnding;

    private void FinishTextEdit()
    {
        if (EditingText is null)
        {
            return;
        }

        TextEditEnding?.Invoke(this, EventArgs.Empty); // the window commits its text box through SetText

        if (EditingText is { } stillOpen)
        {
            SetText(stillOpen, stillOpen.Text); // nobody handled it: keep the text as it is
        }
    }

    // ------------------------------------------------------------------------------------------------- commands

    public void Select(AnnotationItem? item)
    {
        Selected = item;
        OnChanged();
    }

    public void DeleteSelected()
    {
        if (Selected is { } item)
        {
            Remove(item);
            Commit();
            OnChanged();
        }
    }

    public void DuplicateSelected()
    {
        if (Selected is { } item)
        {
            Paste(AnnotationJson.Clone(item));
        }
    }

    /// <summary>Adds a copy of an item (from the clipboard or duplicate), offset so it is visibly separate.</summary>
    public void Paste(AnnotationItem item)
    {
        FinishTextEdit();
        item.Id = Guid.NewGuid().ToString("N"); // a pasted copy must never share the original's identity
        item.Move(12, 12);
        if (item is StepItem step)
        {
            step.Number = Document.NextStepNumber();
        }

        Document.Items.Add(item);
        Selected = item;
        Commit();
        OnChanged();
    }

    public void Nudge(double dx, double dy)
    {
        if (Selected is { } item)
        {
            item.Move(dx, dy);
            Commit($"nudge:{item.Id}");
            OnChanged();
        }
    }

    /// <summary>Moves the selection one step up (+1) or down (-1) in the stacking order, or to the top/bottom.</summary>
    public void Reorder(int step, bool toEnd = false)
    {
        if (Selected is not { } item)
        {
            return;
        }

        var items = Document.Items;
        var from = items.IndexOf(item);
        var to = toEnd ? (step > 0 ? items.Count - 1 : 0) : Math.Clamp(from + step, 0, items.Count - 1);
        if (from < 0 || to == from)
        {
            return;
        }

        items.RemoveAt(from);
        items.Insert(to, item);
        Commit();
        OnChanged();
    }

    /// <summary>Changes the style of the selection, or of the current tool when nothing is selected.</summary>
    /// <param name="change">The style change, e.g. <c>s =&gt; s with { Thickness = 6 }</c>.</param>
    /// <param name="property">Names the property, so repeated changes of it (a slider drag) are one undo step.</param>
    public void ApplyStyle(Func<AnnotationStyle, AnnotationStyle> change, string property)
    {
        // Finish an open text box first, so its text is committed as its own step before the style change.
        FinishTextEdit();
        if (Selected is { } item)
        {
            item.Style = change(item.Style);
            var tool = ToolFor(item);
            _toolStyles[tool] = change(_toolStyles[tool]); // remember it for the next item of this kind
            Commit($"style:{property}:{item.Id}");
        }
        else
        {
            var tool = _tool == EditorTool.Select ? EditorTool.Arrow : _tool;
            _toolStyles[tool] = change(_toolStyles[tool]);
        }

        OnChanged();
    }

    /// <summary>Sets the redaction strength of the selected blur or pixelate item.</summary>
    public void SetRedactStrength(int strength)
    {
        if (Selected is RedactItem redact)
        {
            redact.Strength = Math.Clamp(strength, 2, 40);
            Commit($"strength:{redact.Id}");
            OnChanged();
        }
    }

    public void Undo()
    {
        FinishTextEdit();
        Load(History.Undo());
    }

    public void Redo()
    {
        FinishTextEdit();
        Load(History.Redo());
    }

    public void ResetCrop()
    {
        Document.Crop = null;
        CropDraft = _tool == EditorTool.Crop ? Document.ViewArea : null;
        Commit();
        OnChanged();
    }

    /// <summary>Records the current state as an undo step (after a gesture or command).</summary>
    public void Commit(string? mergeKey = null) => History.Commit(Document, mergeKey);

    // ------------------------------------------------------------------------------------------------- gestures

    private void PressSelect(PointD p)
    {
        if (Selected is { } selected)
        {
            var handle = AnnotationGeometry.HandleAt(selected, p, HandleRadius, HitTolerance);
            if (handle != Handle.None)
            {
                _gesture = new Gesture(GestureKind.Drag, p, selected, AnnotationJson.Clone(selected), handle);
                return;
            }
        }

        var hit = AnnotationGeometry.TopmostAt(Document.Items, p, HitTolerance);
        Selected = hit;
        if (hit is not null)
        {
            _gesture = new Gesture(GestureKind.Drag, p, hit, AnnotationJson.Clone(hit), Handle.Body);
        }
    }

    private void Create(PointD p)
    {
        var style = _toolStyles[_tool];
        AnnotationItem item;
        GestureKind kind;
        switch (_tool)
        {
            case EditorTool.Arrow or EditorTool.Line:
                item = new LineItem { Start = p, End = p, Heads = _tool == EditorTool.Arrow ? ArrowHeads.End : ArrowHeads.None };
                kind = GestureKind.CreateLine;
                break;
            case EditorTool.Rectangle:
                item = new RectangleItem { Rect = new RectD(p.X, p.Y, 0, 0) };
                kind = GestureKind.CreateRect;
                break;
            case EditorTool.Ellipse:
                item = new EllipseItem { Rect = new RectD(p.X, p.Y, 0, 0) };
                kind = GestureKind.CreateRect;
                break;
            case EditorTool.Blur or EditorTool.Pixelate:
                item = new RedactItem
                {
                    Rect = new RectD(p.X, p.Y, 0, 0),
                    Mode = _tool == EditorTool.Blur ? RedactMode.Blur : RedactMode.Pixelate,
                    Strength = _tool == EditorTool.Blur ? 8 : 10,
                };
                kind = GestureKind.CreateRect;
                break;
            case EditorTool.Pen or EditorTool.Highlighter:
                item = new StrokeItem { Points = [p], Kind = _tool == EditorTool.Highlighter ? StrokeKind.Highlighter : StrokeKind.Pen };
                kind = GestureKind.CreateStroke;
                break;
            case EditorTool.Text:
                var text = new TextItem { Position = p, Style = style };
                Document.Items.Add(text);
                Selected = text;
                BeginTextEdit(text);
                return;
            case EditorTool.Step:
                var step = new StepItem { Center = p, Number = Document.NextStepNumber(), Style = style };
                Document.Items.Add(step);
                Selected = step;
                Commit();
                return;
            default:
                return;
        }

        item.Style = style;
        Document.Items.Add(item);
        Selected = item;
        _gesture = new Gesture(kind, p, item, null, Handle.None);
    }

    private void PressCrop(PointD p)
    {
        var current = CropDraft ?? Document.ViewArea;
        var handle = CropHandleAt(current, p);
        var wholeImage = current == new PixelRect(0, 0, Document.Width, Document.Height);

        // Inside an existing crop: move it. While the crop is still the whole image, a drag inside draws a new one.
        if (handle is not Handle.None && !(handle == Handle.Body && wholeImage))
        {
            _gesture = new Gesture(GestureKind.CropDrag, p, null, null, handle) { CropStart = current };
            return;
        }

        _gesture = new Gesture(GestureKind.CropNew, p, null, null, Handle.None);
        CropDraft = ToPixelRect(new RectD(p.X, p.Y, 1, 1));
    }

    private PixelRect DragCrop(Gesture g, PointD p, bool constrain)
    {
        var from = g.CropStart!.Value;
        var proxy = new RectangleItem { Rect = new RectD(from.X, from.Y, from.Width, from.Height) };
        var original = new RectangleItem { Rect = proxy.Rect };
        AnnotationGeometry.Drag(proxy, original, g.Handle, g.Start, p, constrain);
        return ToPixelRect(proxy.Rect);
    }

    private Handle CropHandleAt(PixelRect crop, PointD p)
    {
        var proxy = new RectangleItem { Rect = new RectD(crop.X, crop.Y, crop.Width, crop.Height), Style = new AnnotationStyle { Fill = new Rgba(0, 0, 0) } };
        return AnnotationGeometry.HandleAt(proxy, p, HandleRadius * 1.5, HitTolerance);
    }

    private void ApplyCropDraft()
    {
        if (CropDraft is not { } draft)
        {
            return;
        }

        var image = new PixelRect(0, 0, Document.Width, Document.Height);
        var crop = draft.Intersect(image);
        if (crop.Width < MinimumSize || crop.Height < MinimumSize)
        {
            CropDraft = Document.ViewArea; // too small: keep the previous crop
            return;
        }

        Document.Crop = crop == image ? null : crop;
        CropDraft = crop;
        Commit();
    }

    private PixelRect ToPixelRect(RectD r)
    {
        var rect = PixelRect.FromEdges((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
        return rect.Intersect(new PixelRect(0, 0, Document.Width, Document.Height));
    }

    private void Remove(AnnotationItem item)
    {
        Document.Items.Remove(item);
        if (Selected == item)
        {
            Selected = null;
        }
    }

    private static EditorTool ToolFor(AnnotationItem item) => item switch
    {
        LineItem { Heads: ArrowHeads.None } => EditorTool.Line,
        LineItem => EditorTool.Arrow,
        RectangleItem => EditorTool.Rectangle,
        EllipseItem => EditorTool.Ellipse,
        StrokeItem { Kind: StrokeKind.Highlighter } => EditorTool.Highlighter,
        StrokeItem => EditorTool.Pen,
        TextItem => EditorTool.Text,
        StepItem => EditorTool.Step,
        RedactItem { Mode: RedactMode.Pixelate } => EditorTool.Pixelate,
        RedactItem => EditorTool.Blur,
        _ => EditorTool.Select,
    };

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private enum GestureKind
    {
        Drag,
        CreateLine,
        CreateRect,
        CreateStroke,
        CropNew,
        CropDrag,
    }

    private sealed record Gesture(GestureKind Kind, PointD Start, AnnotationItem? Item, AnnotationItem? Original, Handle Handle)
    {
        public PixelRect? CropStart { get; init; }
    }
}
