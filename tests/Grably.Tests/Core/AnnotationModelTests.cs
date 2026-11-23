using Grably.Core.Annotations;
using Grably.Core.Geometry;
using Grably.Core.Imaging;

namespace Grably.Tests.Core;

public class AnnotationModelTests
{
    public static AnnotationDocument SampleDocument()
    {
        var doc = AnnotationDocument.ForImage(800, 600);
        doc.Crop = new PixelRect(10, 20, 700, 500);
        doc.Items.Add(new LineItem { Start = new(10, 10), End = new(200, 120), Heads = ArrowHeads.End });
        doc.Items.Add(new LineItem { Start = new(10, 300), End = new(300, 300), Style = new AnnotationStyle { Thickness = 2, Shadow = false } });
        doc.Items.Add(new RectangleItem { Rect = new(50, 50, 200, 100), CornerRadius = 6, Style = new AnnotationStyle { Fill = Rgba.Parse("#FFFF0040") } });
        doc.Items.Add(new EllipseItem { Rect = new(300, 50, 120, 80) });
        doc.Items.Add(new StrokeItem { Kind = StrokeKind.Pen, Points = [new(1, 1), new(5, 9.5), new(12.25, 3)] });
        doc.Items.Add(new StrokeItem { Kind = StrokeKind.Highlighter, Points = [new(100, 400), new(400, 400)], Style = new AnnotationStyle { Stroke = Rgba.Parse("#FFE600"), Thickness = 18, Opacity = 0.45 } });
        doc.Items.Add(new TextItem { Position = new(60, 200), Text = "Click here\nthen Save", Width = 240, Background = Rgba.Parse("#FFFFFF") });
        doc.Items.Add(new StepItem { Center = new(500, 300), Number = 1 });
        doc.Items.Add(new StepItem { Center = new(560, 300), Number = 2 });
        doc.Items.Add(new RedactItem { Rect = new(600, 400, 150, 40), Mode = RedactMode.Blur, Strength = 8 });
        doc.Items.Add(new RedactItem { Rect = new(600, 460, 150, 40), Mode = RedactMode.Pixelate, Strength = 12 });
        return doc;
    }

    [Fact]
    public void Every_item_type_survives_a_json_round_trip()
    {
        var doc = SampleDocument();

        var json = AnnotationJson.Serialize(doc);
        var back = AnnotationJson.Deserialize(json);

        Assert.Equal(json, AnnotationJson.Serialize(back));
        Assert.Equal(doc.Items.Select(i => i.GetType()), back.Items.Select(i => i.GetType()));
        Assert.Equal(doc.Crop, back.Crop);
        Assert.Contains("\"type\": \"redact\"", json, StringComparison.Ordinal);
        Assert.Contains("\"stroke\": \"#E81123\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fill\": \"#FFFF0040\"", json, StringComparison.Ordinal);
        Assert.Equal(new PointD(5, 9.5), ((StrokeItem)back.Items[4]).Points[1]);
    }

    [Fact]
    public void Documents_from_a_newer_format_are_refused_with_a_clear_message()
    {
        var json = AnnotationJson.Serialize(new AnnotationDocument { Width = 1, Height = 1, Version = 99 });

        var error = Assert.Throws<NotSupportedException>(() => AnnotationJson.Deserialize(json));
        Assert.Contains("newer Grably", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#E81123", 0xE8, 0x11, 0x23, 0xFF)]
    [InlineData("ff000080", 0xFF, 0, 0, 0x80)]
    public void Colours_parse_and_format(string text, byte r, byte g, byte b, byte a)
    {
        var c = Rgba.Parse(text);

        Assert.Equal(new Rgba(r, g, b, a), c);
        Assert.Equal(text.StartsWith('#') ? text : "#" + text.ToUpperInvariant(), c.ToString());
    }

    [Fact]
    public void Clone_is_deep_and_gets_a_new_id()
    {
        var original = new StrokeItem { Points = [new(1, 1), new(2, 2)] };

        var copy = (StrokeItem)AnnotationJson.Clone(original);
        copy.Move(10, 0);

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(new PointD(1, 1), original.Points[0]);
        Assert.Equal(new PointD(11, 1), copy.Points[0]);
    }

    [Fact]
    public void Step_numbers_continue_after_the_highest()
    {
        var doc = SampleDocument();

        Assert.Equal(3, doc.NextStepNumber());
        Assert.Equal(1, AnnotationDocument.ForImage(10, 10).NextStepNumber());
    }

    [Fact]
    public void Undo_and_redo_walk_through_committed_snapshots()
    {
        var doc = AnnotationDocument.ForImage(100, 100);
        var history = new UndoHistory(doc);
        Assert.False(history.CanUndo);
        Assert.False(history.IsDirty);

        doc.Items.Add(new RectangleItem { Rect = new(1, 1, 10, 10) });
        history.Commit(doc);
        doc.Items[0].Move(5, 5);
        history.Commit(doc);
        history.Commit(doc); // unchanged: ignored

        Assert.True(history.IsDirty);
        Assert.Equal(new RectD(6, 6, 10, 10), ((RectangleItem)history.Current().Items[0]).Rect);

        var undone = history.Undo();
        Assert.Equal(new RectD(1, 1, 10, 10), ((RectangleItem)undone.Items[0]).Rect);
        Assert.Empty(history.Undo().Items);
        Assert.False(history.CanUndo);
        Assert.False(history.IsDirty);

        Assert.Single(history.Redo().Items);
        history.MarkSaved();
        Assert.False(history.IsDirty);

        // A new change after undo drops the redo branch.
        var current = history.Current();
        current.Items.Clear();
        history.Commit(current);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Saved_state_that_falls_off_the_history_counts_as_unsaved()
    {
        var doc = AnnotationDocument.ForImage(10, 10);
        var history = new UndoHistory(doc);
        for (var i = 0; i < UndoHistory.MaxSnapshots + 5; i++)
        {
            doc.Width = 11 + i;
            history.Commit(doc);
        }

        while (history.CanUndo)
        {
            history.Undo();
        }

        Assert.True(history.IsDirty); // the oldest kept state is not what was saved
    }

    [Fact]
    public void Fast_blur_matches_the_shape_of_a_full_blur_and_bilinear_resize_interpolates()
    {
        var image = new PixelBuffer(120, 40);
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 120; x++)
            {
                image.SetRgb(x, y, ((x / 2) + (y / 2)) % 2 == 0 ? 0x000000 : 0xFFFFFF);
            }
        }

        var fast = ImageFilters.FastBlur(image, 30);
        Assert.Equal(image.Size, fast.Size);
        Assert.InRange(fast.GetRgb(60, 20) & 0xFF, 0x60, 0xA0); // fine checkerboard averages to grey

        var tiny = new PixelBuffer(2, 1);
        tiny.SetRgb(0, 0, 0x000000);
        tiny.SetRgb(1, 0, 0xFFFFFF);
        var wide = ImageFilters.ResizeBilinear(tiny, 8, 1);
        Assert.Equal(0x000000, wide.GetRgb(0, 0));
        Assert.Equal(0xFFFFFF, wide.GetRgb(7, 0));
        Assert.InRange(wide.GetRgb(4, 0) & 0xFF, 0x40, 0xC0);
    }

    [Fact]
    public void Project_file_round_trips_image_bytes_and_document()
    {
        var folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "p" + ProjectFile.Extension);
        try
        {
            var image = new PixelBuffer(3, 2);
            image.SetRgb(1, 1, 0x123456);
            using var png = new MemoryStream();
            PngWriter.Write(image, png);
            var doc = SampleDocument();

            var info = new ProjectInfo(@"C:\Pictures\Grably\shot.png", new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc));
            ProjectFile.Save(path, png.ToArray(), doc, info);
            doc.Items.RemoveAt(0);
            ProjectFile.SaveDocument(path, doc);
            var (bytes, loaded, loadedInfo) = ProjectFile.Load(path);

            Assert.Equal(png.ToArray(), bytes);
            Assert.Equal(AnnotationJson.Serialize(doc), AnnotationJson.Serialize(loaded));
            Assert.Equal(info, loadedInfo);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Pixelate_makes_uniform_blocks_and_blur_smooths_edges()
    {
        var image = new PixelBuffer(8, 4);
        for (var x = 0; x < 8; x++)
        {
            for (var y = 0; y < 4; y++)
            {
                image.SetRgb(x, y, x < 4 ? 0x000000 : 0xFFFFFF);
            }
        }

        var pixelated = ImageFilters.Pixelate(image, 4);
        Assert.Equal(0x000000, pixelated.GetRgb(3, 3));
        Assert.Equal(0xFFFFFF, pixelated.GetRgb(4, 0));

        var shifted = ImageFilters.Pixelate(image.Crop(new PixelRect(2, 0, 4, 4)), 4); // half black, half white
        Assert.Equal(0x7F7F7F, shifted.GetRgb(0, 0));

        var blurred = ImageFilters.Blur(image, 2);
        var edge = blurred.GetRgb(3, 1) & 0xFF;
        Assert.InRange(edge, 1, 254); // the hard edge is now a gradient
        Assert.Equal(255, blurred.Pixels[3]); // alpha untouched for opaque input
        Assert.Equal(0x000000, image.GetRgb(3, 1)); // input not modified
    }
}
