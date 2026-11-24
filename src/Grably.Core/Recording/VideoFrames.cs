namespace Grably.Core.Recording;

/// <summary>
/// Frame numbers and times in a constant-frame-rate video: frame N is on screen from N/fps until (N+1)/fps. The trim
/// editor snaps every edit to a frame start, so what the preview shows is exactly where the export cuts.
/// </summary>
public static class VideoFrames
{
    /// <summary>
    /// The frame on screen at <paramref name="time"/>. A time a hair before a frame start (a ten-thousandth of a
    /// frame, as times rounded to 100 ns are) counts as that frame.
    /// </summary>
    public static int Index(TimeSpan time, double fps) => Math.Max(0, (int)Math.Floor((time.TotalSeconds * fps) + 1e-4));

    /// <summary>When frame <paramref name="index"/> appears.</summary>
    public static TimeSpan Start(int index, double fps) => TimeSpan.FromTicks((long)Math.Round(index * TimeSpan.TicksPerSecond / fps));

    /// <summary>The frame start nearest to <paramref name="time"/>.</summary>
    public static TimeSpan Snap(TimeSpan time, double fps) => Start((int)Math.Round(time.TotalSeconds * fps), fps);

    /// <summary>
    /// The time to seek ffmpeg to for frame <paramref name="index"/>: a quarter of a frame early, so rounding in the
    /// time stamps can never skip to the next frame (ffmpeg keeps the first frame at or after the time).
    /// </summary>
    public static TimeSpan SeekTime(int index, double fps) => index <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((index - 0.25) / fps);

    /// <summary>m:ss.ff, or h:mm:ss.ff from an hour.</summary>
    public static string Format(TimeSpan time)
    {
        var hundredths = (long)Math.Floor(time.TotalMilliseconds / 10);
        var (seconds, fraction) = Math.DivRem(Math.Max(0, hundredths), 100);
        var (minutes, s) = Math.DivRem(seconds, 60);
        var (hours, m) = Math.DivRem(minutes, 60);
        return hours > 0 ? $"{hours}:{m:00}:{s:00}.{fraction:00}" : $"{minutes}:{s:00}.{fraction:00}";
    }
}
