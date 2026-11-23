using System.Globalization;
using Grably.Core.Settings;

namespace Grably.Core.Recording;

/// <summary>The ffmpeg command lines Grably runs, built in one place so they can be unit-tested.</summary>
public static class FfmpegArguments
{
    /// <summary>Everything is tagged BT.709 limited range, what players assume for HD video.</summary>
    private static readonly string[] ColorTags =
        ["-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709"];

    /// <summary>
    /// Encodes raw frames read from stdin at a constant frame rate into a Matroska file, which stays playable up to
    /// the last frame written if Grably or the PC crashes. One keyframe per second keeps seeking in the trim editor fast.
    /// </summary>
    public static IReadOnlyList<string> Encode(
        VideoEncoder encoder, RawPixelFormat input, int width, int height, int fps, VideoQuality quality, string output)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats",
            "-f", "rawvideo", "-pix_fmt", input == RawPixelFormat.Nv12 ? "nv12" : "bgra",
            "-video_size", $"{width}x{height}", "-framerate", Number(fps),
        };
        if (input == RawPixelFormat.Nv12)
        {
            args.AddRange(ColorTags); // describes the input: the GPU already produced BT.709 limited range
        }

        args.AddRange(["-i", "pipe:0"]);
        if (input == RawPixelFormat.Bgra)
        {
            // ffmpeg's default RGB to YUV matrix is BT.601; ask for BT.709 so colours match the GPU path.
            args.AddRange(["-vf", $"scale=out_color_matrix=bt709:out_range=tv,format={encoder.PixelFormat}"]);
        }
        else if (encoder.PixelFormat != "nv12")
        {
            args.AddRange(["-vf", $"format={encoder.PixelFormat}"]);
        }

        args.AddRange(["-c:v", encoder.Name]);
        args.AddRange(encoder.QualityOptions(width, height, fps, quality));
        args.AddRange(["-g", Number(fps), "-pix_fmt", encoder.PixelFormat]);
        args.AddRange(ColorTags);
        args.AddRange(["-f", "matroska", "-y", output]);
        return args;
    }

    /// <summary>A short real encode at the output size: the only reliable test that an encoder works here.</summary>
    public static IReadOnlyList<string> Probe(VideoEncoder encoder, int width, int height) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostats",
        "-f", "lavfi", "-i", $"color=c=gray:s={width}x{height}:r=30", "-frames:v", "5",
        "-pix_fmt", encoder.PixelFormat, "-c:v", encoder.Name, .. encoder.QualityOptions(width, height, 30, VideoQuality.High),
        "-f", "null", "-",
    ];

    /// <summary>The finished recording: the Matroska stream copied into MP4, index at the front for quick playback.</summary>
    /// <param name="codec">h264 or hevc; HEVC is tagged hvc1, which Apple players need (ffmpeg's default hev1 is refused).</param>
    public static IReadOnlyList<string> Remux(string input, string output, string codec = "h264") =>
    [
        "-hide_banner", "-loglevel", "error", "-nostats", "-i", input, "-c", "copy",
        .. codec == "hevc" ? (string[])["-tag:v", "hvc1"] : [],
        "-movflags", "+faststart", "-y", output,
    ];

    /// <summary>
    /// The final MP4: the video copied as it is, audio tracks (raw files, each with its ffmpeg input options) mixed
    /// at full level and encoded to AAC. Audio is padded so the video decides the length. Without tracks this is
    /// <see cref="Remux"/>.
    /// </summary>
    public static IReadOnlyList<string> Mux(
        string video, IReadOnlyList<(string Path, IReadOnlyList<string> InputOptions)> audio, string output, string codec = "h264")
    {
        if (audio.Count == 0)
        {
            return Remux(video, output, codec);
        }

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-i", video };
        foreach (var (path, options) in audio)
        {
            args.AddRange(options);
            args.AddRange(["-i", path]);
        }

        var inputs = string.Concat(Enumerable.Range(1, audio.Count).Select(i => $"[{i}:a]"));
        var mix = audio.Count == 1
            ? $"{inputs}apad[a]"
            : $"{inputs}amix=inputs={audio.Count}:duration=longest:normalize=0,alimiter=limit=0.97,apad[a]";
        args.AddRange(["-filter_complex", mix, "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-shortest"]);
        if (codec == "hevc")
        {
            args.AddRange(["-tag:v", "hvc1"]);
        }

        args.AddRange(["-movflags", "+faststart", "-y", output]);
        return args;
    }

    /// <summary>One frame at <paramref name="seconds"/> as an image (checks and previews).</summary>
    public static IReadOnlyList<string> ExtractFrame(string video, double seconds, string output) =>
        ["-hide_banner", "-loglevel", "error", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-y", output];

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
