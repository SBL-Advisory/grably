using Grably.Core.Geometry;

namespace Grably.Tests.Core;

public class PixelGeometryTests
{
    [Fact]
    public void FromCorners_includes_both_corner_pixels_in_any_order()
    {
        Assert.Equal(new PixelRect(0, 0, 11, 11), PixelRect.FromCorners(new(10, 10), new(0, 0)));
        Assert.Equal(new PixelRect(5, 5, 1, 1), PixelRect.FromCorners(new(5, 5), new(5, 5)));
        Assert.Equal(new PixelRect(-20, 3, 31, 8), PixelRect.FromCorners(new(10, 3), new(-20, 10)));
    }

    [Fact]
    public void Right_and_bottom_are_exclusive()
    {
        var r = new PixelRect(10, 20, 30, 40);

        Assert.Equal(40, r.Right);
        Assert.Equal(60, r.Bottom);
        Assert.True(r.Contains(new PixelPoint(39, 59)));
        Assert.False(r.Contains(new PixelPoint(40, 59)));
        Assert.False(r.Contains(new PixelPoint(39, 60)));
    }

    [Fact]
    public void Intersect_and_union()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, -10, 100, 30);

        Assert.Equal(new PixelRect(50, 0, 50, 20), a.Intersect(b));
        Assert.Equal(PixelRect.Empty, a.Intersect(new PixelRect(100, 0, 5, 5))); // touching edges do not overlap
        Assert.Equal(new PixelRect(0, -10, 150, 110), a.Union(b));
        Assert.Equal(a, PixelRect.Empty.Union(a));
    }

    [Fact]
    public void MoveInside_keeps_size_and_stays_within_bounds()
    {
        var bounds = new PixelRect(-1920, 0, 5360, 1440);

        Assert.Equal(new PixelRect(3240, 1240, 200, 200), new PixelRect(3400, 1300, 200, 200).MoveInside(bounds));
        Assert.Equal(new PixelRect(-1920, 0, 200, 200), new PixelRect(-3000, -50, 200, 200).MoveInside(bounds));
    }

    [Fact]
    public void Clamp_keeps_points_on_the_last_pixel_inside()
    {
        var r = new PixelRect(0, 0, 10, 10);

        Assert.Equal(new PixelPoint(9, 0), r.Clamp(new PixelPoint(50, -5)));
    }

    [Fact]
    public void EvenSize_rounds_dimensions_down()
    {
        Assert.Equal(new PixelRect(1, 1, 640, 360), new PixelRect(1, 1, 641, 361).EvenSize());
    }

    [Theory]
    [InlineData("10,20,300,400", true)]
    [InlineData("-1920, 0, 1920, 1080", true)]
    [InlineData("10,20,0,400", false)]
    [InlineData("10,20,300", false)]
    [InlineData("a,b,c,d", false)]
    [InlineData(null, false)]
    public void TryParse(string? text, bool valid) => Assert.Equal(valid, PixelRect.TryParse(text, out _));
}
