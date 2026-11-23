namespace Grably.Core.Imaging;

public enum StitchOutcome
{
    /// <summary>The frame scrolled; its new rows were added.</summary>
    Appended,

    /// <summary>The frame shows the same content as the previous one (the end of the page, or nothing scrolled).</summary>
    NoMovement,

    /// <summary>No reliable overlap with the previous frame (scrolled too far, or the content changed).</summary>
    Lost,

    /// <summary>The image reached its maximum height; nothing more will be added.</summary>
    Full,
}

/// <param name="Offset">How many pixels the content moved up, for <see cref="StitchOutcome.Appended"/>.</param>
public sealed record StitchStep(StitchOutcome Outcome, int Offset);

/// <summary>
/// Joins the frames of a scrolling capture into one tall image. Each frame is compared with the previous one to find
/// how far the content scrolled, and only the rows that came into view are added.
/// <list type="bullet">
/// <item>Comparisons use a band of columns that leaves out the right edge, where the scrollbar thumb moves. Two rows
/// are the same only when nearly every pixel of the band matches, because lines of text can differ in a single
/// digit ("Line 001" and "Line 004").</item>
/// <item>Rows that stay put at the top or bottom of both frames (a sticky header, toolbar or footer) are kept once:
/// the header from the first frame, the footer from the last.</item>
/// <item>The scroll distance is the offset at which the overlapping rows match. A few rows may still differ (a hover
/// highlight under the pointer, a blinking caret). Rows of a single colour say nothing about the offset and are
/// skipped. A quick search on sampled columns proposes offsets; each is confirmed on full rows, and when several
/// fit equally (repeating content), the one nearest the expected distance wins.</item>
/// </list>
/// </summary>
public sealed class ScrollStitcher
{
    public const int DefaultMaxHeight = 30_000;

    /// <summary>Largest per-channel difference that still counts as the same pixel (anti-aliasing, dithering).</summary>
    private const int Tolerance = 24;

    /// <summary>Differing pixels a row may have and still be the same row (a caret, a cursor-shaped artefact).</summary>
    private const int RowNoise = 2;

    /// <summary>Share of compared rows that may differ at the right offset (hover highlights, animations).</summary>
    private const double MaxDifferentRows = 0.15;

    /// <summary>Share of compared rows that may differ when nothing scrolled.</summary>
    private const double StillDifferentRows = 0.05;

    /// <summary>A row is informative when its pixels span at least this much brightness.</summary>
    private const int InformativeContrast = 16;

    private const int MaxSampleColumns = 128;
    private const int MaxVerified = 64;

    private readonly int _width;
    private readonly int _height;
    private readonly int _left;
    private readonly int _right;
    private readonly int[] _samples; // byte offsets within a row of the columns used by the quick search
    private readonly List<byte[]> _rows = [];
    private PixelBuffer _last;
    private bool[] _lastInformative;
    private int _footer = -1; // fixed on the first movement

    public ScrollStitcher(PixelBuffer first, int maxHeight = DefaultMaxHeight)
    {
        _width = first.Width;
        _height = first.Height;
        MaxHeight = maxHeight;

        // The middle of the frame, leaving out the right edge where a scrollbar moves; narrow regions use it all.
        _right = _width - Math.Max(24, _width / 25);
        _left = _width / 20;
        if (_right - _left < 16)
        {
            (_left, _right) = (0, _width);
        }

        var step = Math.Max(1, (_right - _left) / MaxSampleColumns);
        _samples = Enumerable.Range(0, (_right - _left + step - 1) / step).Select(i => (_left + (i * step)) * 4).ToArray();

        _last = first;
        _lastInformative = Informative(first);
        for (var y = 0; y < _height; y++)
        {
            _rows.Add(Row(first, y));
        }
    }

    public int MaxHeight { get; }

    /// <summary>Height of the image so far.</summary>
    public int Height => _rows.Count + Math.Max(0, _footer);

    /// <summary>Adds the next frame, taken after scrolling down.</summary>
    /// <param name="expectedOffset">How far the content probably moved (from earlier steps), to settle ties.</param>
    public StitchStep Add(PixelBuffer frame, int? expectedOffset = null)
    {
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"Frames must all be {_width}x{_height}; got {frame.Width}x{frame.Height}.", nameof(frame));
        }

        if (Height >= MaxHeight)
        {
            return new StitchStep(StitchOutcome.Full, 0);
        }

        var pair = new Pair(_last, _lastInformative, frame, Informative(frame));
        var (header, footer) = StaticBands(pair);
        if (header + footer >= _height || DifferentRows(pair, 0, header, footer, full: true, StillDifferentRows) <= StillDifferentRows)
        {
            (_last, _lastInformative) = (frame, pair.NewInformative);
            return new StitchStep(StitchOutcome.NoMovement, 0);
        }

        if (FindOffset(pair, header, footer, expectedOffset) is not { } d)
        {
            return new StitchStep(StitchOutcome.Lost, 0); // the previous frame stays the reference
        }

        (_last, _lastInformative) = (frame, pair.NewInformative);
        if (_footer < 0)
        {
            // The first movement shows which bottom rows stay put; they are added once, from the last frame.
            _footer = Math.Min(footer, _height - d);
            _rows.RemoveRange(_rows.Count - _footer, _footer);
        }

        var from = _height - _footer - d;
        var count = Math.Min(d, MaxHeight - Height);
        for (var y = from; y < from + count; y++)
        {
            _rows.Add(Row(frame, y));
        }

        return new StitchStep(count < d ? StitchOutcome.Full : StitchOutcome.Appended, d);
    }

    /// <summary>The stitched image: everything added so far, with the footer of the last frame at the bottom.</summary>
    public PixelBuffer Result()
    {
        var footer = Math.Max(0, _footer);
        var image = new PixelBuffer(_width, _rows.Count + footer);
        var stride = _width * 4;
        for (var y = 0; y < _rows.Count; y++)
        {
            Buffer.BlockCopy(_rows[y], 0, image.Pixels, y * stride, stride);
        }

        Buffer.BlockCopy(_last.Pixels, (_height - footer) * stride, image.Pixels, _rows.Count * stride, footer * stride);
        return image;
    }

    private int? FindOffset(Pair pair, int header, int footer, int? expected)
    {
        var zone = _height - header - footer;
        var minOverlap = Math.Max(4, zone / 10);

        // Quick search on sampled columns: every offset that might fit.
        var candidates = new List<(int Offset, double Share)>();
        for (var d = 1; d <= zone - minOverlap; d++)
        {
            var share = DifferentRows(pair, d, header, footer, full: false, MaxDifferentRows);
            if (share <= MaxDifferentRows)
            {
                candidates.Add((d, share));
            }
        }

        // Confirm on full rows; sampled columns cannot tell apart lines that differ in one character.
        var confirmed = candidates
            .OrderBy(c => c.Share)
            .ThenBy(c => expected is { } e ? Math.Abs(c.Offset - e) : c.Offset)
            .Take(MaxVerified)
            .Select(c => (c.Offset, Share: DifferentRows(pair, c.Offset, header, footer, full: true, MaxDifferentRows)))
            .Where(c => c.Share <= MaxDifferentRows)
            .ToList();
        if (confirmed.Count == 0)
        {
            return null;
        }

        var best = confirmed.Min(c => c.Share);
        var ties = confirmed.Where(c => c.Share <= best + 0.01).ToList();
        return expected is { } hint
            ? ties.MinBy(c => Math.Abs(c.Offset - hint)).Offset
            : ties.MinBy(c => (c.Share, c.Offset)).Offset;
    }

    /// <summary>
    /// Share of rows that differ when the new frame's row r is laid over the previous frame's row r + d, counting only
    /// rows with something on them. Above <paramref name="budget"/> it stops early and returns infinity.
    /// </summary>
    private double DifferentRows(Pair pair, int d, int header, int footer, bool full, double budget)
    {
        var end = _height - footer - d;
        var allowed = budget * Math.Max(1, end - header);
        var compared = 0;
        var different = 0;
        for (var r = header; r < end; r++)
        {
            if (!pair.NewInformative[r] && !pair.OldInformative[r + d])
            {
                continue;
            }

            compared++;
            if (!SameRow(pair.New, r, pair.Old, r + d, full) && ++different > allowed)
            {
                return double.PositiveInfinity;
            }
        }

        return compared < 3 ? double.PositiveInfinity : (double)different / compared;
    }

    /// <summary>Rows at the top and bottom that are the same in both frames (sticky header and footer).</summary>
    private (int Header, int Footer) StaticBands(Pair pair)
    {
        var header = 0;
        while (header < _height && SameRow(pair.Old, header, pair.New, header, full: true))
        {
            header++;
        }

        if (header == _height)
        {
            return (_height, 0);
        }

        var footer = 0;
        while (footer < _height - header && SameRow(pair.Old, _height - 1 - footer, pair.New, _height - 1 - footer, full: true))
        {
            footer++;
        }

        return (header, footer);
    }

    /// <summary>Whether two rows match across the band (every column, or the sampled ones for the quick search).</summary>
    private bool SameRow(PixelBuffer a, int rowA, PixelBuffer b, int rowB, bool full)
    {
        var pa = a.Pixels;
        var pb = b.Pixels;
        var ia = rowA * _width * 4;
        var ib = rowB * _width * 4;
        var differing = 0;
        if (full)
        {
            for (var c = _left * 4; c < _right * 4; c += 4)
            {
                if (Differs(pa, ia + c, pb, ib + c) && ++differing > RowNoise)
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var c in _samples)
        {
            if (Differs(pa, ia + c, pb, ib + c))
            {
                return false; // samples are sparse: any difference counts
            }
        }

        return true;
    }

    private static bool Differs(byte[] pa, int i, byte[] pb, int j) =>
        Math.Abs(pa[i] - pb[j]) > Tolerance || Math.Abs(pa[i + 1] - pb[j + 1]) > Tolerance || Math.Abs(pa[i + 2] - pb[j + 2]) > Tolerance;

    /// <summary>Rows with any contrast across the band (text, edges); single-colour rows fit any offset.</summary>
    private bool[] Informative(PixelBuffer image)
    {
        var result = new bool[_height];
        var p = image.Pixels;
        for (var y = 0; y < _height; y++)
        {
            int min = 255, max = 0;
            var i = y * _width * 4;
            for (var c = _left * 4; c < _right * 4 && max - min < InformativeContrast; c += 4)
            {
                var luma = ((p[i + c + 2] * 3) + (p[i + c + 1] * 6) + p[i + c]) / 10;
                min = Math.Min(min, luma);
                max = Math.Max(max, luma);
            }

            result[y] = max - min >= InformativeContrast;
        }

        return result;
    }

    private byte[] Row(PixelBuffer image, int y)
    {
        var row = new byte[_width * 4];
        Buffer.BlockCopy(image.Pixels, y * _width * 4, row, 0, row.Length);
        return row;
    }

    /// <summary>The previous frame and the new one, with which of their rows are informative.</summary>
    private sealed record Pair(PixelBuffer Old, bool[] OldInformative, PixelBuffer New, bool[] NewInformative);
}
