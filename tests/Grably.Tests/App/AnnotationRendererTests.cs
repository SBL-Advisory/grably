using System.Windows.Media.Imaging;
using Grably.App.Editor;
using Grably.App.Imaging;
using Grably.Core.Annotations;
using Grably.Core.Geometry;
using Grably.Core.Imaging;

namespace Grably.Tests.App;

/// <summary>Flattens documents with one item each over a plain image and checks the actual output pixels.</summary>
public class AnnotationRendererTests
{
    private const int White = 0xFFFFFF;
    private static readonly AnnotationStyle Plain = new() { Shadow = false };

    [Fact]
    public void Rectangle_outline_is_drawn_and_the_inside_left_alone() => StaRunner.Run(() =>
    {
        var result = Render(new RectangleItem { Rect = new(20, 20, 100, 60), Style = Plain });

        Assert.True(IsRed(result.GetRgb(20, 50)), $"edge was #{result.GetRgb(20, 50):X6}");
        Assert.Equal(White, result.GetRgb(70, 50));
    });

    [Fact]
    public void Filled_shapes_fill_their_inside() => StaRunner.Run(() =>
    {
        var result = Render(new EllipseItem { Rect = new(20, 20, 100, 60), Style = Plain with { Fill = new Rgba(0, 0, 255) } });

        Assert.Equal(0x0000FF, result.GetRgb(70, 50));
        Assert.True(IsRed(result.GetRgb(120, 50)) || IsRed(result.GetRgb(119, 50)));
        Assert.Equal(White, result.GetRgb(22, 22)); // box corner, outside the ellipse
    });

    [Fact]
    public void Arrow_has_a_solid_head_at_the_end() => StaRunner.Run(() =>
    {
        var result = Render(new LineItem { Start = new(10, 100), End = new(180, 100), Heads = ArrowHeads.End, Style = Plain });

        Assert.True(IsRed(result.GetRgb(60, 100)));
        Assert.True(IsRed(result.GetRgb(172, 100)));      // inside the head
        Assert.True(IsRed(result.GetRgb(160, 106)));      // head is wider than the 4 px shaft
        Assert.Equal(White, result.GetRgb(60, 106));      // shaft is not
        Assert.Equal(White, result.GetRgb(186, 100));     // nothing past the tip
    });

    [Fact]
    public void Freehand_pen_and_translucent_highlighter() => StaRunner.Run(() =>
    {
        // Smoothing rounds sharp corners off (it goes through the midpoints), so sample a straight run.
        var pen = Render(new StrokeItem { Points = [new(10, 10), new(50, 50), new(100, 100), new(150, 150)], Style = Plain });
        Assert.True(IsRed(pen.GetRgb(75, 75)), $"stroke pixel was #{pen.GetRgb(75, 75):X6}");
        Assert.Equal(White, pen.GetRgb(75, 90));

        var marker = Render(new StrokeItem
        {
            Kind = StrokeKind.Highlighter,
            Points = [new(10, 50), new(190, 50)],
            Style = new AnnotationStyle { Stroke = Rgba.Parse("#FFE600"), Thickness = 18, Opacity = 0.5 },
        });
        var c = marker.GetRgb(100, 50);
        Assert.True((c & 0xFF) is > 0x40 and < 0xC0, $"highlighter should blend with white, got #{c:X6}");
        Assert.True(c >> 16 > 0xF0);
    });

    [Fact]
    public void Text_draws_glyphs_inside_its_measured_box() => StaRunner.Run(() =>
    {
        var text = new TextItem { Position = new(10, 10), Text = "Hello", Style = Plain, Background = new Rgba(0, 0, 0) };

        var result = Render(text);

        Assert.NotNull(text.MeasuredSize);
        var box = text.Bounds;
        Assert.InRange(box.Width, 40, 120);
        Assert.Equal(0x000000, result.GetRgb((int)box.X + 2, (int)box.Bottom - 3)); // background corner area
        Assert.True(CountRed(result, box) > 20, "expected red glyph pixels");
    });

    [Fact]
    public void Step_badge_is_a_filled_circle_with_its_number() => StaRunner.Run(() =>
    {
        var step = new StepItem { Center = new(100, 100), Number = 7, Style = Plain };

        var result = Render(step);

        Assert.True(IsRed(result.GetRgb(100 - (int)step.Radius + 4, 100)));
        Assert.True(CountWhiteish(result, step.Bounds) > 10, "expected the white number");
        Assert.Equal(White, result.GetRgb(100, 100 - (int)step.Radius - 4));
    });

    [Fact]
    public void Pixelate_and_blur_change_only_their_area() => StaRunner.Run(() =>
    {
        var image = Checkerboard(200, 200, 2);

        var pixelated = Flatten(image, new RedactItem { Rect = new(40, 40, 40, 40), Mode = RedactMode.Pixelate, Strength = 10 });
        Assert.Equal(pixelated.GetRgb(40, 40), pixelated.GetRgb(49, 49)); // one 10 px block
        Assert.Equal(image.GetRgb(10, 10), pixelated.GetRgb(10, 10));     // outside untouched

        var blurred = Flatten(image, new RedactItem { Rect = new(100, 100, 60, 60), Mode = RedactMode.Blur, Strength = 6 });
        var mid = blurred.GetRgb(130, 130) & 0xFF;
        Assert.InRange(mid, 0x60, 0xA0); // a 2 px checkerboard blurs to grey
        Assert.Equal(image.GetRgb(98, 98), blurred.GetRgb(98, 98));
    });

    [Fact]
    public void Crop_sets_the_output_size_and_offset() => StaRunner.Run(() =>
    {
        var image = Checkerboard(200, 200, 25);
        var doc = AnnotationDocument.ForImage(200, 200);
        doc.Crop = new PixelRect(30, 40, 100, 50);

        var result = AnnotationRenderer.Flatten(doc, image).ToPixelBuffer();

        Assert.Equal(new PixelSize(100, 50), result.Size);
        Assert.Equal(image.GetRgb(30, 40), result.GetRgb(0, 0));
        Assert.Equal(image.GetRgb(129, 89), result.GetRgb(99, 49));
    });

    [Fact]
    public void Shadow_darkens_just_below_right_of_a_shape() => StaRunner.Run(() =>
    {
        var line = new LineItem { Start = new(20, 100), End = new(180, 100), Style = new AnnotationStyle { Thickness = 4 } };
        var withShadow = Render(line);
        line.Style = line.Style with { Shadow = false };
        var without = Render(line);

        Assert.True((withShadow.GetRgb(100, 104) & 0xFF) < 0xFF, "shadow pixel should be darker than white");
        Assert.Equal(White, without.GetRgb(100, 104));
    });

    [Fact]
    public void Opacity_applies_to_the_whole_item() => StaRunner.Run(() =>
    {
        var result = Render(new RectangleItem { Rect = new(20, 20, 100, 60), Style = Plain with { Opacity = 0.5, Fill = new Rgba(0, 0, 0) } });

        var c = result.GetRgb(70, 50) & 0xFF;
        Assert.InRange(c, 0x70, 0x90);
    });

    private static PixelBuffer Render(AnnotationItem item) => Flatten(Solid(200, 200, White), item);

    private static PixelBuffer Flatten(PixelBuffer image, AnnotationItem item)
    {
        var doc = AnnotationDocument.ForImage(image.Width, image.Height);
        doc.Items.Add(item);
        BitmapSource result = AnnotationRenderer.Flatten(doc, image);
        Assert.Equal(image.Width, result.PixelWidth);
        return result.ToPixelBuffer();
    }

    private static PixelBuffer Solid(int w, int h, int rgb)
    {
        var image = new PixelBuffer(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                image.SetRgb(x, y, rgb);
            }
        }

        return image;
    }

    private static PixelBuffer Checkerboard(int w, int h, int cell)
    {
        var image = new PixelBuffer(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                image.SetRgb(x, y, ((x / cell) + (y / cell)) % 2 == 0 ? 0x000000 : 0xFFFFFF);
            }
        }

        return image;
    }

    private static bool IsRed(int rgb) => (rgb >> 16 & 0xFF) > 0xC0 && (rgb >> 8 & 0xFF) < 0x60 && (rgb & 0xFF) < 0x60;

    private static int CountRed(PixelBuffer image, RectD area) => Count(image, area, IsRed);

    private static int CountWhiteish(PixelBuffer image, RectD area) => Count(image, area, c => (c & 0xFF) > 0xE0 && (c >> 8 & 0xFF) > 0xE0);

    private static int Count(PixelBuffer image, RectD area, Func<int, bool> match)
    {
        var n = 0;
        for (var y = Math.Max(0, (int)area.Y); y < Math.Min(image.Height, (int)area.Bottom); y++)
        {
            for (var x = Math.Max(0, (int)area.X); x < Math.Min(image.Width, (int)area.Right); x++)
            {
                n += match(image.GetRgb(x, y)) ? 1 : 0;
            }
        }

        return n;
    }
}
