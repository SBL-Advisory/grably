namespace Grably.Core.Text;

/// <summary>A line of recognised text, with its vertical extent in the original image's pixels.</summary>
public sealed record OcrLine(string Text, double Top, double Bottom)
{
    public double Center => (Top + Bottom) / 2;
}

/// <summary>A horizontal strip of the image to recognise, and the part of it whose lines it owns (original pixels).</summary>
/// <param name="OwnFrom">Lines whose centre falls in [OwnFrom, OwnTo) come from this strip.</param>
public sealed record OcrTile(int Top, int Height, int OwnFrom, int OwnTo);

/// <summary>
/// How to feed an image to Windows OCR. The engine reads small screen text better when it is enlarged, refuses
/// images beyond its maximum dimension, and in practice misses text in tall, mostly empty images well before that
/// limit (a 10,000-px strip with one line returns nothing). So images are read in short overlapping strips, each
/// enlarged on its own, and the lines are joined back without duplicates.
/// </summary>
public static class OcrLayout
{
    /// <summary>Strip height after scaling: tall enough for a few paragraphs, short enough to be read reliably.</summary>
    public const int StripHeight = 2000;

    /// <summary>Overlap between strips in original pixels, so a line cut by one strip's edge is whole in the next.</summary>
    public const int Overlap = 120;

    /// <summary>Images up to this wide are enlarged 2x (screen text is small for OCR); wider ones are read as they are.</summary>
    private const int UpscaleMaxWidth = 1600;

    /// <summary>2 for narrow images, 1 for wide ones, less than 1 for images wider than the engine allows.</summary>
    public static double ChooseScale(int width, int maxDimension) =>
        width > maxDimension ? (double)maxDimension / width
        : width <= UpscaleMaxWidth && width * 2 <= maxDimension ? 2
        : 1;

    /// <summary>
    /// Strips (in original pixels) that are at most <see cref="StripHeight"/> tall once scaled; each row is owned by
    /// exactly one strip.
    /// </summary>
    public static IReadOnlyList<OcrTile> Tiles(int height, double scale, int maxDimension)
    {
        var stripHeight = Math.Max(Overlap * 2, (int)(Math.Min(StripHeight, maxDimension) / scale));
        if (height <= stripHeight)
        {
            return [new OcrTile(0, height, 0, height)];
        }

        var tiles = new List<OcrTile>();
        var step = stripHeight - Overlap;
        for (var top = 0; ; top += step)
        {
            var tileHeight = Math.Min(stripHeight, height - top);
            var last = top + tileHeight >= height;
            var ownFrom = top == 0 ? 0 : top + (Overlap / 2);
            var ownTo = last ? height : top + step + (Overlap / 2);
            tiles.Add(new OcrTile(top, tileHeight, ownFrom, ownTo));
            if (last)
            {
                return tiles;
            }
        }
    }

    /// <summary>
    /// Keeps the lines each strip owns, strip by strip, each in the engine's reading order (which handles columns
    /// better than sorting by position would).
    /// </summary>
    /// <param name="tileLines">Lines per strip in top-to-bottom order, positions in original pixels of the whole image.</param>
    public static IReadOnlyList<OcrLine> Join(IEnumerable<(OcrTile Tile, IReadOnlyList<OcrLine> Lines)> tileLines) =>
        tileLines.SelectMany(t => t.Lines.Where(l => l.Center >= t.Tile.OwnFrom && l.Center < t.Tile.OwnTo)).ToList();

    public static string ToText(IEnumerable<OcrLine> lines) =>
        string.Join(Environment.NewLine, lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0));
}
