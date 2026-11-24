using Grably.Core.Annotations;
using Grably.Core.Geometry;

namespace Grably.Tests.Core;

public class AnnotationEditorTests
{
    private static AnnotationEditor NewEditor() => new(AnnotationDocument.ForImage(800, 600));

    private static void Drag(AnnotationEditor editor, PointD from, PointD to, EditModifiers modifiers = EditModifiers.None)
    {
        editor.Press(from, modifiers);
        editor.Move(new((from.X + to.X) / 2, (from.Y + to.Y) / 2), modifiers);
        editor.Release(to, modifiers);
    }

    [Theory]
    [InlineData(EditorTool.Rectangle, typeof(RectangleItem))]
    [InlineData(EditorTool.Ellipse, typeof(EllipseItem))]
    [InlineData(EditorTool.Blur, typeof(RedactItem))]
    [InlineData(EditorTool.Pixelate, typeof(RedactItem))]
    public void Shape_tools_create_their_item_from_a_drag(EditorTool tool, Type expected)
    {
        var editor = NewEditor();
        editor.Tool = tool;

        Drag(editor, new(100, 100), new(40, 160));

        var item = Assert.Single(editor.Document.Items);
        Assert.IsType(expected, item);
        Assert.Equal(new RectD(40, 100, 60, 60), ((IRectItem)item).Rect);
        Assert.Same(item, editor.Selected);
        Assert.True(editor.History.CanUndo);
    }

    [Fact]
    public void Arrow_tool_creates_an_arrow_and_shift_snaps_its_angle()
    {
        var editor = NewEditor();

        Drag(editor, new(0, 0), new(100, 95), EditModifiers.Shift);

        var arrow = Assert.IsType<LineItem>(Assert.Single(editor.Document.Items));
        Assert.Equal(ArrowHeads.End, arrow.Heads);
        Assert.Equal(arrow.End.X, arrow.End.Y, 6);
    }

    [Fact]
    public void A_click_with_a_shape_tool_creates_nothing()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Rectangle;

        editor.Press(new(50, 50));
        editor.Release(new(51, 51));

        Assert.Empty(editor.Document.Items);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void Pen_collects_points_and_shift_makes_a_straight_highlight()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Pen;
        editor.Press(new(0, 0));
        editor.Move(new(5, 5));
        editor.Move(new(5.5, 5)); // under one pixel: skipped
        editor.Move(new(10, 0));
        editor.Release(new(10, 0));
        Assert.Equal(3, ((StrokeItem)editor.Document.Items[0]).Points.Count);

        editor.Tool = EditorTool.Highlighter;
        editor.Press(new(0, 100), EditModifiers.Shift);
        editor.Move(new(50, 120), EditModifiers.Shift);
        editor.Release(new(200, 101), EditModifiers.Shift);
        var highlight = (StrokeItem)editor.Document.Items[1];
        Assert.Equal(StrokeKind.Highlighter, highlight.Kind);
        Assert.Equal([new PointD(0, 100), new PointD(200, 101)], highlight.Points);
        Assert.Equal(18, highlight.Style.Thickness);
    }

    [Fact]
    public void Step_tool_numbers_badges_in_order()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Step;

        editor.Press(new(10, 10));
        editor.Release(new(10, 10));
        editor.Press(new(60, 10));
        editor.Release(new(60, 10));

        Assert.Equal([1, 2], editor.Document.Items.Cast<StepItem>().Select(s => s.Number));
    }

    [Fact]
    public void Text_tool_opens_a_text_box_and_empty_text_is_dropped()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Text;
        TextItem? requested = null;
        editor.TextEditRequested += (_, t) => requested = t;

        editor.Press(new(30, 40));
        Assert.NotNull(requested);
        Assert.Same(requested, editor.EditingText);
        editor.SetText(requested, "Hello\r\nworld");

        Assert.Equal("Hello\nworld", ((TextItem)Assert.Single(editor.Document.Items)).Text);
        Assert.Null(editor.EditingText);

        editor.Press(new(300, 300));
        editor.SetText(requested = editor.EditingText!, "  ");
        Assert.Single(editor.Document.Items);
    }

    [Fact]
    public void Clicking_existing_text_with_the_text_tool_edits_it()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Text;
        editor.Press(new(30, 40));
        editor.SetText(editor.EditingText!, "Note");

        TextItem? requested = null;
        editor.TextEditRequested += (_, t) => requested = t;
        editor.Press(new(35, 45));

        Assert.Same(editor.Document.Items[0], requested);
        Assert.Single(editor.Document.Items);
    }

    [Fact]
    public void Select_tool_picks_moves_and_resizes()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Rectangle;
        Drag(editor, new(100, 100), new(200, 150));
        editor.Tool = EditorTool.Select;
        editor.Select(null);

        Drag(editor, new(100, 125), new(110, 135)); // on the left edge: pick and move
        var rect = (RectangleItem)editor.Document.Items[0];
        Assert.Same(rect, editor.Selected);
        Assert.Equal(new RectD(110, 110, 100, 50), rect.Rect);

        Drag(editor, new(210, 160), new(260, 200)); // bottom-right handle
        Assert.Equal(new RectD(110, 110, 150, 90), rect.Rect);

        editor.Press(new(600, 500)); // empty space deselects
        editor.Release(new(600, 500));
        Assert.Null(editor.Selected);
    }

    [Fact]
    public void Handles_of_the_selection_work_with_any_tool()
    {
        var editor = NewEditor();
        Drag(editor, new(0, 0), new(100, 0)); // arrow, selected

        Drag(editor, new(100, 0), new(100, 80)); // drag its end handle, still with the arrow tool

        var arrow = Assert.IsType<LineItem>(Assert.Single(editor.Document.Items));
        Assert.Equal(new PointD(100, 80), arrow.End);
    }

    [Fact]
    public void Crop_tool_sets_a_crop_and_reset_removes_it()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Crop;
        Assert.Equal(new PixelRect(0, 0, 800, 600), editor.CropDraft);

        Drag(editor, new(400, 300), new(500, 350)); // inside the full-image draft: not a handle, starts a new crop
        Assert.Equal(new PixelRect(400, 300, 100, 50), editor.Document.Crop);

        Drag(editor, new(500, 350), new(600, 400)); // bottom-right handle of the crop
        Assert.Equal(new PixelRect(400, 300, 200, 100), editor.Document.Crop);

        editor.ResetCrop();
        Assert.Null(editor.Document.Crop);
        editor.Undo();
        Assert.Equal(new PixelRect(400, 300, 200, 100), editor.Document.Crop);
    }

    [Fact]
    public void Reorder_duplicate_paste_and_delete()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Step;
        editor.Press(new(10, 10));
        editor.Release(new(10, 10));
        editor.Tool = EditorTool.Rectangle;
        Drag(editor, new(0, 0), new(50, 50));
        var rect = editor.Selected!;

        editor.Reorder(-1);
        Assert.Same(rect, editor.Document.Items[0]);
        editor.Reorder(+1, toEnd: true);
        Assert.Same(rect, editor.Document.Items[^1]);

        editor.Select(editor.Document.Items[0]); // the step
        editor.DuplicateSelected();
        var copy = Assert.IsType<StepItem>(editor.Selected);
        Assert.Equal(2, copy.Number);
        Assert.Equal(new PointD(22, 22), copy.Center);

        editor.DeleteSelected();
        Assert.Equal(2, editor.Document.Items.Count);
        Assert.Null(editor.Selected);
    }

    [Fact]
    public void Style_changes_apply_to_the_selection_and_are_remembered_for_the_tool()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Rectangle;
        Drag(editor, new(0, 0), new(50, 50));

        for (var t = 5; t <= 9; t++)
        {
            editor.ApplyStyle(s => s with { Thickness = t }, "thickness"); // a slider drag
        }

        Assert.Equal(9, editor.Selected!.Style.Thickness);
        editor.Undo(); // the whole drag is one step
        Assert.Equal(4, ((RectangleItem)editor.Document.Items[0]).Style.Thickness);

        editor.Select(null);
        Drag(editor, new(100, 100), new(150, 150));
        Assert.Equal(9, editor.Selected!.Style.Thickness); // the tool remembers the last style
    }

    [Fact]
    public void Pasted_items_get_their_own_identity()
    {
        var editor = NewEditor();
        Drag(editor, new(0, 0), new(100, 0));
        var original = editor.Selected!;

        editor.Paste(AnnotationJson.DeserializeItem(AnnotationJson.Serialize(original))); // same id, as from the clipboard

        Assert.NotEqual(original.Id, editor.Selected!.Id);
        Assert.Equal(2, editor.Document.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void Clearing_an_existing_text_is_an_undoable_change()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Text;
        editor.Press(new(30, 40));
        editor.SetText(editor.EditingText!, "Keep me");
        editor.History.MarkSaved();

        editor.DoubleClick(new(35, 45));
        editor.SetText(editor.EditingText!, string.Empty);

        Assert.Empty(editor.Document.Items);
        Assert.True(editor.History.IsDirty);
        editor.Undo();
        Assert.Equal("Keep me", ((TextItem)Assert.Single(editor.Document.Items)).Text);
    }

    [Fact]
    public void A_style_change_while_typing_first_commits_the_text_box()
    {
        var editor = NewEditor();
        editor.TextEditEnding += (_, _) => editor.SetText(editor.EditingText!, "typed"); // what the window does
        editor.Tool = EditorTool.Text;
        editor.Press(new(30, 40));

        editor.ApplyStyle(s => s with { Stroke = new Rgba(0, 0, 255) }, "color");

        var text = (TextItem)Assert.Single(editor.Document.Items);
        Assert.Equal("typed", text.Text);
        Assert.Equal(new Rgba(0, 0, 255), text.Style.Stroke);
        editor.Undo(); // undoes the colour only
        Assert.Equal("typed", ((TextItem)Assert.Single(editor.Document.Items)).Text);
    }

    [Fact]
    public void Without_a_text_box_an_unfinished_empty_text_is_dropped_not_committed()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Text;
        editor.Press(new(30, 40));

        editor.ApplyStyle(s => s with { Thickness = 9 }, "thickness");

        Assert.Empty(editor.Document.Items);
        Assert.Null(editor.EditingText);
        Assert.False(editor.History.CanUndo);
    }

    [Fact]
    public void An_abandoned_gesture_ends_where_the_pointer_got_to()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Rectangle;
        editor.Press(new(10, 10));
        editor.Move(new(80, 60));
        Assert.True(editor.IsGestureActive);

        editor.EndGesture(); // mouse capture lost

        Assert.False(editor.IsGestureActive);
        Assert.Equal(new RectD(10, 10, 70, 50), ((RectangleItem)Assert.Single(editor.Document.Items)).Rect);
        Assert.True(editor.History.IsDirty);
    }

    [Fact]
    public void Undo_and_redo_keep_the_selection_when_the_item_still_exists()
    {
        var editor = NewEditor();
        editor.Tool = EditorTool.Ellipse;
        Drag(editor, new(0, 0), new(50, 50));
        var id = editor.Selected!.Id;
        editor.Nudge(10, 0);
        editor.Nudge(10, 0); // merged with the previous nudge

        editor.Undo();
        Assert.Equal(id, editor.Selected?.Id);
        Assert.Equal(0, ((EllipseItem)editor.Document.Items[0]).Rect.X);

        editor.Undo();
        Assert.Empty(editor.Document.Items);
        Assert.Null(editor.Selected);

        editor.Redo();
        Assert.Single(editor.Document.Items);
    }
}
