namespace Grably.Core.Recording;

/// <summary>
/// Keeps a recording at a constant frame rate on one clock: frame N belongs to time N / fps. When a frame is made
/// late, it is written as many times as needed to catch up, so the video always lasts as long as the recording did
/// and stays in step with audio recorded on the same clock.
/// </summary>
public sealed class FramePacer(int fps)
{
    public int FrameRate { get; } = fps > 0 ? fps : throw new ArgumentOutOfRangeException(nameof(fps));

    /// <summary>Frames handed out so far (including repeats).</summary>
    public long Frames { get; private set; }

    /// <summary>Frames written again because the next one was not ready in time.</summary>
    public long Repeats { get; private set; }

    /// <summary>When the next frame is due, from the start of the recording.</summary>
    public TimeSpan NextDue => TimeSpan.FromSeconds((double)Frames / FrameRate);

    /// <summary>
    /// A frame was made at <paramref name="elapsed"/>: how many times to write it so the frame count matches the clock
    /// (at least once; more when the frames that were due meanwhile were missed).
    /// </summary>
    public int Take(TimeSpan elapsed)
    {
        var due = (long)Math.Floor(elapsed.TotalSeconds * FrameRate) + 1;
        var count = (int)Math.Max(1, due - Frames);
        Frames += count;
        Repeats += count - 1;
        return count;
    }

    /// <summary>The recording time the frames written so far cover.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / FrameRate);
}

/// <summary>The size frames are encoded at.</summary>
public static class OutputSize
{
    /// <summary>
    /// The source size, scaled down to fit <paramref name="maxHeight"/> and <paramref name="maxWidth"/> if given, and
    /// rounded down to even numbers (H.264 and HEVC in 4:2:0 need even sizes).
    /// </summary>
    public static (int Width, int Height) For(int sourceWidth, int sourceHeight, int? maxHeight = null, int? maxWidth = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        var scale = 1.0;
        if (maxHeight is { } h && sourceHeight > h)
        {
            scale = Math.Min(scale, (double)h / sourceHeight);
        }

        if (maxWidth is { } w && sourceWidth > w)
        {
            scale = Math.Min(scale, (double)w / sourceWidth);
        }

        return (Even(sourceWidth * scale), Even(sourceHeight * scale));
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Floor(value) & ~1);
}

/// <summary>The colour bars recorded by the test pattern, to check colours survive conversion and encoding.</summary>
public static class TestPattern
{
    /// <summary>White, yellow, cyan, green, magenta, red, blue, black, as 0xRRGGBB, left to right over the top 80 %.</summary>
    public static IReadOnlyList<int> Bars { get; } = [0xFFFFFF, 0xFFFF00, 0x00FFFF, 0x00FF00, 0xFF00FF, 0xFF0000, 0x0000FF, 0x000000];
}
