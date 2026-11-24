using Grably.Core.Annotations;

namespace Grably.Tests.Core;

public class AnnotationGeometryTests
{
    [Fact]
    public void Lines_are_hit_near_the_segment_only()
    {
        var line = new LineItem { Start = new(0, 0), End = new(100, 0) };

        Assert.True(AnnotationGeometry.HitTest(line, new(50, 3), 2));
        Assert.False(AnnotationGeometry.HitTest(line, new(50, 10), 2));
        Assert.False(AnnotationGeometry.HitTest(line, new(110, 0), 2));
    }

    [Fact]
    public void Unfilled_shapes_are_hit_on_the_outline_and_filled_ones_anywhere()
    {
        var empty = new RectangleItem { Rect = new(0, 0, 100, 100) };
        var filled = new RectangleItem { Rect = new(0, 0, 100, 100), Style = new AnnotationStyle { Fill = new Rgba(0, 0, 0) } };

        Assert.True(AnnotationGeometry.HitTest(empty, new(1, 50), 3));
        Assert.False(AnnotationGeometry.HitTest(empty, new(50, 50), 3));
        Assert.True(AnnotationGeometry.HitTest(filled, new(50, 50), 3));

        var ellipse = new EllipseItem { Rect = new(0, 0, 200, 100) };
        Assert.True(AnnotationGeometry.HitTest(ellipse, new(200, 50), 3));
        Assert.True(AnnotationGeometry.HitTest(ellipse, new(100, 1), 3));
        Assert.False(AnnotationGeometry.HitTest(ellipse, new(100, 50), 3));
        Assert.False(AnnotationGeometry.HitTest(ellipse, new(5, 5), 3)); // inside the box, outside the ellipse
    }

    [Fact]
    public void Topmost_item_wins()
    {
        var below = new RectangleItem { Rect = new(0, 0, 100, 100), Style = new AnnotationStyle { Fill = new Rgba(1, 1, 1) } };
        var above = new StepItem { Center = new(50, 50) };

        Assert.Same(above, AnnotationGeometry.TopmostAt([below, above], new(50, 50), 2));
        Assert.Same(below, AnnotationGeometry.TopmostAt([below, above], new(5, 95), 2));
        Assert.Null(AnnotationGeometry.TopmostAt([below, above], new(300, 300), 2));
    }

    [Fact]
    public void Corner_handle_drag_resizes_and_flipping_past_the_anchor_normalises()
    {
        var rect = new RectangleItem { Rect = new(10, 10, 100, 50) };
        var original = AnnotationJson.Clone(rect);

        AnnotationGeometry.Drag(rect, original, Handle.BottomRight, new(110, 60), new(150, 90), constrain: false);
        Assert.Equal(new RectD(10, 10, 140, 80), rect.Rect);

        AnnotationGeometry.Drag(rect, original, Handle.BottomRight, new(110, 60), new(0, 0), constrain: false);
        Assert.Equal(new RectD(0, 0, 10, 10), rect.Rect);

        AnnotationGeometry.Drag(rect, original, Handle.BottomRight, new(110, 60), new(150, 80), constrain: true);
        Assert.Equal(new RectD(10, 10, 140, 140), rect.Rect); // square from the opposite corner
    }

    [Fact]
    public void Body_drag_moves_from_the_original_position_and_shift_locks_an_axis()
    {
        var step = new StepItem { Center = new(50, 50) };
        var original = AnnotationJson.Clone(step);

        AnnotationGeometry.Drag(step, original, Handle.Body, new(50, 50), new(70, 55), constrain: false);
        AnnotationGeometry.Drag(step, original, Handle.Body, new(50, 50), new(80, 60), constrain: false); // not cumulative
        Assert.Equal(new PointD(80, 60), step.Center);

        AnnotationGeometry.Drag(step, original, Handle.Body, new(50, 50), new(80, 60), constrain: true);
        Assert.Equal(new PointD(80, 50), step.Center);
    }

    [Fact]
    public void Line_end_drag_snaps_to_45_degrees_with_shift()
    {
        var line = new LineItem { Start = new(0, 0), End = new(10, 0) };
        var original = AnnotationJson.Clone(line);

        AnnotationGeometry.Drag(line, original, Handle.End, new(10, 0), new(100, 90), constrain: true);

        Assert.Equal(line.End.X, line.End.Y, 6);
        Assert.Equal(new PointD(0, 0), line.Start);
    }

    [Fact]
    public void Handles_exist_for_rect_items_and_line_ends_only()
    {
        Assert.Equal(8, AnnotationGeometry.Handles(new RedactItem { Rect = new(0, 0, 10, 10) }).Count);
        Assert.Equal(2, AnnotationGeometry.Handles(new LineItem()).Count);
        Assert.Empty(AnnotationGeometry.Handles(new TextItem { Text = "x" }));

        var rect = new RectangleItem { Rect = new(0, 0, 100, 100) };
        Assert.Equal(Handle.BottomRight, AnnotationGeometry.HandleAt(rect, new(98, 101), 5, 2));
        Assert.Equal(Handle.Body, AnnotationGeometry.HandleAt(rect, new(50, 50), 5, 2)); // inside the box of a selected item
        Assert.Equal(Handle.None, AnnotationGeometry.HandleAt(rect, new(150, 150), 5, 2));
    }
}
