using Grably.Core.Imaging;

namespace Grably.DevCli;

/// <summary>A generated tall page and the frames a scrolling capture of it would see.</summary>
/// <remarks>Also compiled into the unit tests (linked file).</remarks>
internal static class SyntheticPage
{
    /// <summary>Blocks of "text" rows with blank gaps and a few identical repeated blocks, on white.</summary>
    public static PixelBuffer Create(int width, int height)
    {
        var page = new PixelBuffer(width, height);
        var random = new Random(7);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                page.SetRgb(x, y, 0xFFFFFF);
            }
        }

        var top = 20;
        var block = 0;
        while (top < height - 40)
        {
            var lines = 2 + random.Next(4);
            var repeat = block % 5 == 3; // every fifth block looks exactly like the one before it
            var seed = repeat ? block - 1 : block;
            var lineRandom = new Random(seed * 31);
            for (var l = 0; l < lines && top < height - 20; l++, top += 18)
            {
                var length = 120 + lineRandom.Next(width - 200);
                var color = 0x202020 + (lineRandom.Next(4) * 0x101010);
                for (var y = top; y < top + 9; y++)
                {
                    for (var x = 24; x < 24 + length; x++)
                    {
                        if ((x / 7) % 6 != 5) // gaps between "words"
                        {
                            page.SetRgb(x, y, color);
                        }
                    }
                }
            }

            top += 14 + random.Next(30);
            block++;
        }

        return page;
    }

    /// <summary>
    /// Viewports of the page at the given scroll offsets, under a sticky header and above a sticky footer, with a
    /// scrollbar thumb on the right that moves with the offset.
    /// </summary>
    public static List<PixelBuffer> Frames(PixelBuffer page, int viewport, int header, int footer, int[] offsets)
    {
        var frames = new List<PixelBuffer>();
        var content = viewport - header - footer;
        foreach (var offset in offsets)
        {
            var frame = new PixelBuffer(page.Width, viewport);
            for (var y = 0; y < viewport; y++)
            {
                for (var x = 0; x < page.Width; x++)
                {
                    var rgb = y < header ? (x < 200 && y is > 16 and < 30 ? 0xFFFFFF : 0x0E9AA7)
                        : y >= viewport - footer ? (y % 8 == 0 ? 0x888888 : 0xDDDDDD)
                        : page.GetRgb(x, offset + (y - header));
                    frame.SetRgb(x, y, rgb);
                }
            }

            var thumbTop = header + (offset * (content - 60) / Math.Max(1, page.Height - content));
            for (var y = thumbTop; y < thumbTop + 60; y++)
            {
                for (var x = page.Width - 12; x < page.Width - 4; x++)
                {
                    frame.SetRgb(x, y, 0x777777);
                }
            }

            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>What a perfect stitch gives: header, the page rows that were shown, footer.</summary>
    public static PixelBuffer Expected(PixelBuffer page, int header, int footer, int viewport, int lastOffset)
    {
        var shown = Math.Min(page.Height, lastOffset + (viewport - header - footer));
        var image = new PixelBuffer(page.Width, header + shown + footer);
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                var rgb = y < header ? (x < 200 && y is > 16 and < 30 ? 0xFFFFFF : 0x0E9AA7)
                    : y >= header + shown ? ((y - (header + shown) + (viewport - footer)) % 8 == 0 ? 0x888888 : 0xDDDDDD)
                    : page.GetRgb(x, y - header);
                image.SetRgb(x, y, rgb);
            }
        }

        return image;
    }

    /// <summary>Rows that differ, ignoring the right edge where the scrollbar is drawn.</summary>
    public static int Difference(PixelBuffer a, PixelBuffer b, int ignoreRight)
    {
        var rows = 0;
        for (var y = 0; y < Math.Min(a.Height, b.Height); y++)
        {
            for (var x = 0; x < Math.Min(a.Width, b.Width) - ignoreRight; x++)
            {
                if (a.GetRgb(x, y) != b.GetRgb(x, y))
                {
                    rows++;
                    break;
                }
            }
        }

        return rows + Math.Abs(a.Height - b.Height);
    }
}
