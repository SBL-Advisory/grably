using Grably.Core.Imaging;
using Grably.DevCli;

namespace Grably.Tests.Core;

public class ScrollStitcherTests
{
    private static readonly PixelBuffer Page = SyntheticPage.Create(width: 480, height: 2200);

    private static PixelBuffer Stitch(IReadOnlyList<PixelBuffer> frames, out List<StitchStep> steps, int maxHeight = ScrollStitcher.DefaultMaxHeight)
    {
        var stitcher = new ScrollStitcher(frames[0], maxHeight);
        steps = frames.Skip(1).Select(f => stitcher.Add(f)).ToList();
        return stitcher.Result();
    }

    [Fact]
    public void A_page_with_sticky_header_footer_and_a_moving_scrollbar_is_reproduced_exactly()
    {
        int[] offsets = [0, 120, 290, 500, 640, 860, 1050, 1260, 1460, 1600, 1600];
        var frames = SyntheticPage.Frames(Page, viewport: 420, header: 40, footer: 30, offsets);

        var result = Stitch(frames, out var steps);

        var expected = SyntheticPage.Expected(Page, header: 40, footer: 30, viewport: 420, lastOffset: 1600);
        Assert.Equal(expected.Height, result.Height);
        Assert.Equal(0, SyntheticPage.Difference(result, expected, ignoreRight: 40));
        Assert.Equal([120, 170, 210, 140, 220, 190, 210, 200, 140], steps.Take(9).Select(s => s.Offset));
        Assert.Equal(StitchOutcome.NoMovement, steps[^1].Outcome);
    }

    [Fact]
    public void Without_sticky_bars_every_row_comes_from_the_page()
    {
        int[] offsets = [0, 100, 250, 400];
        var frames = SyntheticPage.Frames(Page, viewport: 300, header: 0, footer: 0, offsets);

        var result = Stitch(frames, out _);

        Assert.Equal(400 + 300, result.Height);
        Assert.Equal(0, SyntheticPage.Difference(result, Page.Crop(new(0, 0, Page.Width, 700)), ignoreRight: 40));
    }

    [Fact]
    public void A_frame_with_no_overlap_is_reported_lost_and_changes_nothing()
    {
        var frames = SyntheticPage.Frames(Page, viewport: 300, header: 0, footer: 0, [0, 1200]);
        var stitcher = new ScrollStitcher(frames[0]);

        var step = stitcher.Add(frames[1]);

        Assert.Equal(StitchOutcome.Lost, step.Outcome);
        Assert.Equal(300, stitcher.Result().Height);
    }

    [Fact]
    public void An_identical_frame_is_no_movement()
    {
        var frames = SyntheticPage.Frames(Page, viewport: 300, header: 20, footer: 0, [500, 500]);

        Stitch(frames, out var steps);

        Assert.Equal(new StitchStep(StitchOutcome.NoMovement, 0), steps.Single());
    }

    [Fact]
    public void The_image_stops_growing_at_the_maximum_height()
    {
        var frames = SyntheticPage.Frames(Page, viewport: 300, header: 0, footer: 0, [0, 200, 400, 600]);

        var result = Stitch(frames, out var steps, maxHeight: 550);

        Assert.Equal(550, result.Height);
        Assert.Equal(StitchOutcome.Full, steps[1].Outcome);
        Assert.Equal(StitchOutcome.Full, steps[2].Outcome);
    }

    [Fact]
    public void Repeating_content_is_resolved_with_the_expected_distance()
    {
        // Stripes repeating every 40 rows: offsets 60, 100 and 140 all fit the same; the hint picks the real one.
        var stripes = new PixelBuffer(200, 1000);
        for (var y = 0; y < stripes.Height; y++)
        {
            for (var x = 0; x < stripes.Width; x++)
            {
                stripes.SetRgb(x, y, (y % 40) < 6 && x % 9 < 6 ? 0x303030 : 0xFFFFFF);
            }
        }

        var frames = SyntheticPage.Frames(stripes, viewport: 200, header: 0, footer: 0, [0, 100]);
        var stitcher = new ScrollStitcher(frames[0]);

        var step = stitcher.Add(frames[1], expectedOffset: 95);

        Assert.Equal(100, step.Offset);
    }

    [Fact]
    public void Frames_of_another_size_are_refused()
    {
        var stitcher = new ScrollStitcher(new PixelBuffer(100, 100));

        Assert.Throws<ArgumentException>(() => stitcher.Add(new PixelBuffer(100, 120)));
    }
}
