using Grably.Core.Imaging;
using Grably.Core.Text;

namespace Grably.Tests.Core;

public class OcrLayoutTests
{
    [Theory]
    [InlineData(800, 2.0)] // narrow screen text is enlarged, however tall (it is read strip by strip)
    [InlineData(1600, 2.0)]
    [InlineData(3440, 1.0)] // a wide full screen is read as it is
    [InlineData(20000, 0.5)] // too wide for the engine
    public void Scale_enlarges_narrow_images_and_only_shrinks_too_wide_ones(int width, double expected) =>
        Assert.Equal(expected, OcrLayout.ChooseScale(width, maxDimension: 10000));

    [Theory]
    [InlineData(500, 1.0)]
    [InlineData(2000, 1.0)]
    [InlineData(2001, 1.0)]
    [InlineData(25000, 1.0)]
    [InlineData(25000, 2.0)]
    [InlineData(999, 2.0)]
    public void Strips_are_short_once_scaled_and_every_row_has_exactly_one_owner(int height, double scale)
    {
        var tiles = OcrLayout.Tiles(height, scale, maxDimension: 10000);

        Assert.All(tiles, t => Assert.InRange(t.Height * scale, 1, OcrLayout.StripHeight));
        Assert.Equal(height, tiles[^1].Top + tiles[^1].Height);
        Assert.Equal(0, tiles[0].OwnFrom);
        Assert.Equal(height, tiles[^1].OwnTo);
        for (var i = 1; i < tiles.Count; i++)
        {
            Assert.Equal(tiles[i - 1].OwnTo, tiles[i].OwnFrom); // owned ranges touch without gaps or overlap
            Assert.InRange(tiles[i].OwnFrom, tiles[i].Top, tiles[i].Top + tiles[i].Height);
            Assert.InRange(tiles[i - 1].OwnTo, tiles[i - 1].Top, tiles[i - 1].Top + tiles[i - 1].Height);
        }
    }

    [Fact]
    public void A_line_seen_by_two_strips_is_kept_once()
    {
        var tiles = OcrLayout.Tiles(3000, 1, 10000); // owners split at 1940
        var shared = new OcrLine("both see me", 1925, 1945);
        var joined = OcrLayout.Join(
            [
                (tiles[0], [new OcrLine("first", 10, 30), shared]),
                (tiles[1], [shared, new OcrLine("last", 2900, 2920)]),
            ]);

        Assert.Equal(["first", "both see me", "last"], joined.Select(l => l.Text));
        Assert.Equal("first" + Environment.NewLine + "both see me" + Environment.NewLine + "last", OcrLayout.ToText(joined));
    }

    [Fact]
    public void Resizing_keeps_flat_colours_and_blends_edges()
    {
        var image = new PixelBuffer(2, 1);
        image.SetRgb(0, 0, 0x000000);
        image.SetRgb(1, 0, 0xFFFFFF);

        var doubled = ImageResize.Resize(image, 4, 2);

        Assert.Equal((4, 2), (doubled.Width, doubled.Height));
        Assert.Equal(0x000000, doubled.GetRgb(0, 0));
        Assert.Equal(0xFFFFFF, doubled.GetRgb(3, 1));
        Assert.InRange(doubled.GetRgb(1, 0) & 0xFF, 0x30, 0x50); // a quarter of the way to white
        Assert.Equal(255, doubled.Pixels[3]); // alpha kept
    }
}
