using Grably.Core.Capture;
using Grably.Core.Geometry;

namespace Grably.Tests.Core;

public class RegionSelectionTests
{
    // Two monitors side by side, like a 100 % laptop panel left of a 125 % ultrawide.
    private static readonly MonitorInfo Left = new(0, "L", new PixelRect(-1920, 0, 1920, 1080), new PixelRect(-1920, 0, 1920, 1032), 96, false);
    private static readonly MonitorInfo Main = new(1, "M", new PixelRect(0, 0, 3440, 1440), new PixelRect(0, 0, 3440, 1380), 120, true);
    private static readonly PixelRect Desktop = new(-1920, 0, 5360, 1440);

    private static readonly WindowInfo Dialog = new(2, "Dialog", 10, "app", "Dlg", new PixelRect(500, 400, 300, 200));
    private static readonly WindowInfo Editor = new(1, "Editor", 11, "code", "Ed", new PixelRect(0, 0, 3440, 1380));
    private static readonly WindowInfo Offscreen = new(3, "Half off", 10, "app", "X", new PixelRect(3300, 1300, 400, 400));

    private static RegionSelection Create(bool adjust = false) =>
        new(Desktop, [Left, Main], [Dialog, Editor, Offscreen], adjust);

    [Fact]
    public void Hover_finds_the_topmost_window_under_the_pointer()
    {
        var s = Create();

        s.MovePointer(new PixelPoint(600, 500));
        Assert.Equal(Dialog, s.HoverWindow);

        s.MovePointer(new PixelPoint(100, 100));
        Assert.Equal(Editor, s.HoverWindow);

        s.MovePointer(new PixelPoint(-100, 100));
        Assert.Null(s.HoverWindow);
    }

    [Fact]
    public void Click_captures_the_hovered_window()
    {
        var s = Create();
        s.MovePointer(new PixelPoint(600, 500));

        s.Press(new PixelPoint(600, 500));
        s.Release(new PixelPoint(602, 501)); // jitter below the drag threshold is still a click

        Assert.Equal(SelectionPhase.Done, s.Phase);
        Assert.Equal(Dialog.Bounds, s.Result);
        Assert.Equal(Dialog, s.SourceWindow);
    }

    [Fact]
    public void Click_on_a_window_partly_off_screen_captures_only_its_visible_part()
    {
        var s = Create();
        s.MovePointer(new PixelPoint(3400, 1420));
        s.Press(new PixelPoint(3400, 1420));
        s.Release(new PixelPoint(3400, 1420));

        Assert.Equal(new PixelRect(3300, 1300, 140, 140), s.Result);
    }

    [Fact]
    public void Click_on_bare_desktop_captures_that_monitor()
    {
        var s = Create();
        s.MovePointer(new PixelPoint(-500, 500));
        s.Press(new PixelPoint(-500, 500));
        s.Release(new PixelPoint(-500, 500));

        Assert.Equal(Left.Bounds, s.Result);
        Assert.Null(s.SourceWindow);
    }

    [Fact]
    public void Drag_selects_a_region_across_monitors_and_completes_on_release()
    {
        var s = Create();
        var completed = 0;
        s.Completed += (_, _) => completed++;

        s.Press(new PixelPoint(-100, 50));
        s.MovePointer(new PixelPoint(-90, 55));
        Assert.Equal(SelectionPhase.Dragging, s.Phase);
        s.MovePointer(new PixelPoint(199, 249));
        Assert.Equal(new PixelRect(-100, 50, 300, 200), s.Selection);
        s.Release(new PixelPoint(199, 249));

        Assert.Equal(new PixelRect(-100, 50, 300, 200), s.Result);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void Drag_is_clamped_to_the_desktop()
    {
        var s = Create();
        s.Press(new PixelPoint(3000, 1000));
        s.Release(new PixelPoint(9000, 9000));

        Assert.Equal(PixelRect.FromEdges(3000, 1000, 3440, 1440), s.Result);
    }

    [Fact]
    public void Adjust_mode_keeps_the_selection_until_confirmed()
    {
        var s = Create(adjust: true);
        s.Press(new PixelPoint(100, 100));
        s.Release(new PixelPoint(299, 199));

        Assert.Equal(SelectionPhase.Adjusting, s.Phase);
        Assert.Null(s.Result);

        s.Nudge(5, -10, resize: false);
        Assert.Equal(new PixelRect(105, 90, 200, 100), s.Selection);
        s.Nudge(10, 10, resize: true);
        Assert.Equal(new PixelRect(105, 90, 210, 110), s.Selection);

        s.Confirm();
        Assert.Equal(SelectionPhase.Done, s.Phase);
        Assert.Equal(new PixelRect(105, 90, 210, 110), s.Result);
    }

    [Fact]
    public void Adjust_mode_moves_and_resizes_with_the_pointer()
    {
        var s = Create(adjust: true);
        s.Press(new PixelPoint(100, 100));
        s.Release(new PixelPoint(299, 199)); // 200x100 at 100,100

        // Drag the middle: move by (50, 20).
        s.Press(new PixelPoint(200, 150));
        Assert.Equal(SelectionPhase.Moving, s.Phase);
        s.MovePointer(new PixelPoint(250, 170));
        s.Release(new PixelPoint(250, 170));
        Assert.Equal(new PixelRect(150, 120, 200, 100), s.Selection);

        // Drag the bottom-right handle.
        Assert.Equal(ResizeEdges.Right | ResizeEdges.Bottom, s.HitHandle(new PixelPoint(349, 219)));
        s.Press(new PixelPoint(349, 219));
        Assert.Equal(SelectionPhase.Resizing, s.Phase);
        s.MovePointer(new PixelPoint(399, 299));
        s.Release(new PixelPoint(399, 299));
        Assert.Equal(new PixelRect(150, 120, 250, 180), s.Selection);

        // Dragging the left handle past the right edge leaves at least one pixel.
        s.Press(new PixelPoint(150, 210));
        s.MovePointer(new PixelPoint(900, 210));
        s.Release(new PixelPoint(900, 210));
        Assert.Equal(1, s.Selection!.Value.Width);
    }

    [Fact]
    public void Pressing_outside_the_selection_in_adjust_mode_starts_again()
    {
        var s = Create(adjust: true);
        s.Press(new PixelPoint(100, 100));
        s.Release(new PixelPoint(299, 199));

        s.Press(new PixelPoint(1000, 1000));
        s.MovePointer(new PixelPoint(1099, 1049));

        Assert.Equal(SelectionPhase.Dragging, s.Phase);
        Assert.Equal(new PixelRect(1000, 1000, 100, 50), s.Selection);
    }

    [Fact]
    public void Cancel_ends_without_a_result_and_ignores_further_input()
    {
        var s = Create();
        s.Press(new PixelPoint(0, 0));
        s.MovePointer(new PixelPoint(100, 100));

        s.Cancel();
        s.Release(new PixelPoint(200, 200));

        Assert.Equal(SelectionPhase.Done, s.Phase);
        Assert.Null(s.Result);
    }

    [Fact]
    public void Handles_on_a_narrow_selection_pick_one_edge()
    {
        var s = Adjusting(new PixelRect(100, 100, 10, 201)); // narrower than two handle tolerances

        Assert.Equal(ResizeEdges.Top, s.HitHandle(new PixelPoint(105, 100)));
        Assert.Equal(ResizeEdges.Left | ResizeEdges.Top, s.HitHandle(new PixelPoint(100, 100)));
        Assert.Equal(ResizeEdges.Right | ResizeEdges.Bottom, s.HitHandle(new PixelPoint(109, 300)));
        Assert.Equal(ResizeEdges.Right, s.HitHandle(new PixelPoint(112, 200)));

        // Dragging the top handle up keeps the width.
        s.Press(new PixelPoint(105, 100));
        s.MovePointer(new PixelPoint(105, 50));
        s.Release(new PixelPoint(105, 50));
        Assert.Equal(new PixelRect(100, 50, 10, 251), s.Selection);
    }

    [Fact]
    public void A_press_inside_a_tiny_selection_moves_it()
    {
        var s = Adjusting(new PixelRect(100, 100, 12, 12));

        Assert.Equal(ResizeEdges.None, s.HitHandle(new PixelPoint(106, 106)));
        s.Press(new PixelPoint(106, 106));
        Assert.Equal(SelectionPhase.Moving, s.Phase);
        s.MovePointer(new PixelPoint(116, 126));
        s.Release(new PixelPoint(116, 126));

        Assert.Equal(new PixelRect(110, 120, 12, 12), s.Selection);
    }

    [Fact]
    public void A_new_selection_forgets_the_window_clicked_before()
    {
        var s = Create(adjust: true);
        s.MovePointer(new PixelPoint(600, 500));
        s.Press(new PixelPoint(600, 500));
        s.Release(new PixelPoint(600, 500)); // clicked Dialog: adjusting its bounds
        Assert.Equal(Dialog, s.SourceWindow);

        s.Press(new PixelPoint(2000, 1000)); // outside: a new drag over Editor
        s.MovePointer(new PixelPoint(2100, 1100));
        s.Release(new PixelPoint(2100, 1100));
        s.Confirm();

        Assert.Equal(Editor, s.SourceWindow);
    }

    [Fact]
    public void Completed_fires_once_however_it_ends()
    {
        var s = Create();
        var completed = 0;
        s.Completed += (_, _) => completed++;

        s.Press(new PixelPoint(0, 0));
        s.Release(new PixelPoint(100, 100));
        s.Cancel();
        s.Confirm();

        Assert.Equal(1, completed);
        Assert.NotNull(s.Result);
    }

    [Fact]
    public void Nudge_does_nothing_outside_adjust_mode_and_stays_on_the_desktop()
    {
        var s = Create();
        s.Nudge(10, 10, resize: false);
        Assert.Null(s.Selection);

        var adjusting = Adjusting(new PixelRect(3400, 1400, 40, 40));
        adjusting.Nudge(100, 100, resize: false);
        Assert.Equal(new PixelRect(3400, 1400, 40, 40), adjusting.Selection);
    }

    private static RegionSelection Adjusting(PixelRect area)
    {
        var s = Create(adjust: true);
        s.Press(area.Location);
        s.Release(new PixelPoint(area.Right - 1, area.Bottom - 1));
        Assert.Equal(area, s.Selection);
        return s;
    }

    [Fact]
    public void Source_of_a_dragged_region_is_the_window_under_its_centre()
    {
        var s = Create();
        s.Press(new PixelPoint(520, 420));
        s.Release(new PixelPoint(680, 560));

        Assert.Equal(Dialog, s.SourceWindow);
    }
}
