using Grably.App.Editor;
using Grably.Core.Annotations;
using Grably.Core.Geometry;
using Grably.Core.Imaging;

namespace Grably.Tests.App;

public class EditorTextSourceTests
{
    [Fact]
    public void Copy_text_reads_only_what_the_crop_keeps()
    {
        var original = new PixelBuffer(200, 100);
        original.SetRgb(150, 80, 0xFF0000); // inside the crop below
        var document = AnnotationDocument.ForImage(200, 100);
        document.Crop = new PixelRect(100, 50, 80, 40);

        var read = EditorService.TextSource(original, document);

        Assert.Equal((80, 40), (read.Width, read.Height));
        Assert.Equal(0xFF0000, read.GetRgb(50, 30));
    }

    [Fact]
    public void Without_a_crop_the_whole_screenshot_is_read()
    {
        var original = new PixelBuffer(200, 100);

        Assert.Same(original, EditorService.TextSource(original, AnnotationDocument.ForImage(200, 100)));
    }
}
