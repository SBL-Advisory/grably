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
    /// Encodes raw frames read from stdin at a constant frame rate into a Matroska file, which stays playable if
    /// Grably or the PC crashes. One keyframe per second keeps seeking in the trim editor fast.
    /// </summary>
    /// <remarks>
    /// The muxer otherwise holds up to five seconds (a cluster) in memory, and the file's start in its write buffer:
    /// a crash a few seconds in left an empty file. Closing a cluster every second and flushing after every packet
    /// keeps the loss to about a second.
    /// </remarks>
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
        args.AddRange(["-f", "matroska", "-cluster_time_limit", "1000", "-flush_packets", "1", "-y", output]);
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

    /// <summary>
    /// The edited video: each kept part cut out frame-accurately (<c>trim</c>/<c>atrim</c>, decoding everything)
    /// and joined (<c>concat</c>), scaled down if asked, then encoded again: MP4 with the given encoder and AAC, or a
    /// GIF with its own palette. Progress goes to stdout (<c>-progress pipe:1</c>). The format is named, so
    /// <paramref name="output"/> can have any extension (exports are written under a temporary name).
    /// </summary>
    /// <remarks>
    /// Edits are on frame starts (N / fps), but a video's time stamps are rounded: Matroska, and so every recording,
    /// keeps whole milliseconds, which puts frame 1 at 0.033 s, before 1/30 s. The picture is therefore cut half a frame
    /// early, between two frames, where rounding cannot move a frame across the cut. Sound is cut at the exact times.
    /// </remarks>
    /// <param name="hasAudio">The source has a sound track to keep (MP4 only).</param>
    public static IReadOnlyList<string> Export(
        string input, EditList edits, ExportSettings settings, bool hasAudio, VideoEncoder? encoder, VideoQuality quality,
        int outputWidth, int outputHeight, int fps, string output)
    {
        var withAudio = hasAudio && settings.Format == ExportFormat.Mp4;
        var graph = new System.Text.StringBuilder();
        var joined = new System.Text.StringBuilder();
        var halfFrame = TimeSpan.FromSeconds(0.5 / Math.Max(1, fps));
        for (var i = 0; i < edits.Kept.Count; i++)
        {
            var part = edits.Kept[i];
            var (videoStart, videoEnd) = (Seconds(part.Start > halfFrame ? part.Start - halfFrame : TimeSpan.Zero), Seconds(part.End - halfFrame));
            graph.Append(CultureInfo.InvariantCulture, $"[0:v]trim=start={videoStart}:end={videoEnd},setpts=PTS-STARTPTS[v{i}];");
            joined.Append(CultureInfo.InvariantCulture, $"[v{i}]");
            if (withAudio)
            {
                graph.Append(CultureInfo.InvariantCulture, $"[0:a]atrim=start={Seconds(part.Start)}:end={Seconds(part.End)},asetpts=PTS-STARTPTS[a{i}];");
                joined.Append(CultureInfo.InvariantCulture, $"[a{i}]");
            }
        }

        graph.Append(CultureInfo.InvariantCulture, $"{joined}concat=n={edits.Kept.Count}:v=1:a={(withAudio ? 1 : 0)}[cv]{(withAudio ? "[a]" : "")};");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-i", input };
        if (settings.Format == ExportFormat.Gif)
        {
            graph.Append(CultureInfo.InvariantCulture,
                $"[cv]fps={settings.GifFrameRate},scale={outputWidth}:-2:flags=lanczos,split[g0][g1];[g0]palettegen=stats_mode=diff[p];[g1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[v]");
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[v]", "-loop", "0", "-f", "gif", "-y", output]);
            return args;
        }

        var encoderUsed = encoder ?? throw new ArgumentNullException(nameof(encoder), "MP4 export needs an encoder.");
        graph.Append(CultureInfo.InvariantCulture, $"[cv]scale={outputWidth}:{outputHeight}:flags=lanczos,format={encoderUsed.PixelFormat}[v]");
        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[v]"]);
        if (withAudio)
        {
            args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);
        }

        args.AddRange(["-c:v", encoderUsed.Name]);
        args.AddRange(encoderUsed.QualityOptions(outputWidth, outputHeight, fps, quality));
        args.AddRange(["-g", Number(fps), "-r", Number(fps)]);
        args.AddRange(ColorTags);
        if (encoderUsed.Codec == "hevc")
        {
            args.AddRange(["-tag:v", "hvc1"]);
        }

        args.AddRange(["-movflags", "+faststart", "-f", "mp4", "-y", output]);
        return args;
    }

    /// <summary>
    /// A strip of <paramref name="count"/> thumbnails across the video, each <paramref name="height"/> pixels high.
    /// Past two minutes only keyframes are decoded (one a second in Grably's videos), which is many times faster.
    /// </summary>
    public static IReadOnlyList<string> ThumbnailStrip(string video, TimeSpan duration, int count, int height, string output)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (duration > TimeSpan.FromMinutes(2))
        {
            args.AddRange(["-skip_frame", "nokey"]);
        }

        var rate = (count / Math.Max(0.1, duration.TotalSeconds)).ToString("0.######", CultureInfo.InvariantCulture);
        args.AddRange(["-i", video, "-frames:v", "1", "-an", "-vf", $"fps={rate},scale=-2:{height},tile={count}x1", "-y", output]);
        return args;
    }

    /// <summary>A picture of the sound wave, for the timeline (on a square-root scale, so quiet sound still shows).</summary>
    public static IReadOnlyList<string> Waveform(string video, int width, int height, string output) =>
    [
        "-hide_banner", "-loglevel", "error", "-i", video, "-frames:v", "1", "-vn",
        "-filter_complex", $"aformat=channel_layouts=mono,showwavespic=s={width}x{height}:colors=0x{BrandColors.Orange:X6}:scale=sqrt", "-y", output,
    ];

    /// <summary>
    /// The exact frame at <paramref name="time"/> as a BMP on stdout (quick to write and read; seeking before the input
    /// still decodes from the previous keyframe and keeps the first frame at or after the time, and recordings have a
    /// keyframe every second). <paramref name="maxWidth"/> scales larger frames down for the preview.
    /// </summary>
    public static IReadOnlyList<string> FrameAt(string video, TimeSpan time, int? maxWidth = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-ss", Seconds(time), "-i", video, "-frames:v", "1", "-an" };
        if (maxWidth is { } width)
        {
            args.AddRange(["-vf", $"scale='min({Number(width)},iw)':-2:flags=bicubic"]);
        }

        args.AddRange(["-pix_fmt", "bgr24", "-f", "image2pipe", "-vcodec", "bmp", "-"]);
        return args;
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>One frame at <paramref name="seconds"/> as an image (checks and previews).</summary>
    public static IReadOnlyList<string> ExtractFrame(string video, double seconds, string output) =>
        ["-hide_banner", "-loglevel", "error", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", video, "-frames:v", "1", "-y", output];

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
