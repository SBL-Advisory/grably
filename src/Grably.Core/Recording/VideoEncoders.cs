using System.Globalization;
using Grably.Core.Settings;

namespace Grably.Core.Recording;

/// <summary>Raw frames as the recorder hands them to ffmpeg.</summary>
public enum RawPixelFormat
{
    /// <summary>4 bytes per pixel, as captured. The fallback when the GPU cannot convert.</summary>
    Bgra,

    /// <summary>Y plane then interleaved UV at half resolution, BT.709 limited range, converted on the GPU.</summary>
    Nv12,
}

/// <summary>An ffmpeg video encoder Grably may use, with its quality settings.</summary>
/// <param name="Name">The ffmpeg encoder name, such as h264_qsv.</param>
/// <param name="Codec">h264 or hevc.</param>
/// <param name="PixelFormat">What the encoder takes (nv12 for all of Grably's candidates).</param>
public sealed record VideoEncoder(string Name, string Codec, bool Hardware, string PixelFormat)
{
    /// <summary>The rate-control options, taken from <c>ffmpeg -h encoder=NAME</c> for each encoder.</summary>
    public IReadOnlyList<string> QualityOptions(int width, int height, int fps, VideoQuality quality)
    {
        var high = quality == VideoQuality.High;
        string Q(int value) => value.ToString(CultureInfo.InvariantCulture);
        return Name switch
        {
            "h264_qsv" or "hevc_qsv" => ["-preset", "faster", "-global_quality", Q(high ? 22 : 27), "-look_ahead", "0"],
            "h264_nvenc" or "hevc_nvenc" => ["-preset", "p4", "-rc", "vbr", "-cq", Q(high ? 23 : 28), "-b:v", "0"],
            "h264_amf" or "hevc_amf" => ["-quality", "balanced", "-rc", "cqp", "-qp_i", Q(high ? 22 : 27), "-qp_p", Q(high ? 24 : 29)],
            "h264_mf" or "hevc_mf" => ["-rate_control", "quality", "-quality", Q(high ? 75 : 60), "-scenario", "display_remoting"],

            // Anything else: a bitrate from the pixel rate (bits per pixel per frame).
            _ => ["-b:v", Q((int)Math.Min(60_000_000, (long)width * height * fps * (high ? 0.12 : 0.07)))],
        };
    }
}

/// <summary>The encoders Grably tries, in order. A listed encoder proves nothing (the driver may be missing), so each is probed with a real encode.</summary>
public static class VideoEncoders
{
    /// <summary>
    /// Hardware H.264 first, then Media Foundation (Windows' own encoder, on the GPU when it can, else in software),
    /// then HEVC for sizes H.264 hardware refuses (NVENC H.264 stops at 4096 pixels wide). Never libx264 (GPL, not in
    /// the bundled build) and not OpenH264: Cisco's patent licence covers only Cisco's own binaries, not one built
    /// into ffmpeg. These encoders come with Windows or the graphics driver, which carry their own licences.
    /// </summary>
    public static IReadOnlyList<VideoEncoder> Candidates { get; } =
    [
        new("h264_nvenc", "h264", Hardware: true, "nv12"),
        new("h264_qsv", "h264", Hardware: true, "nv12"),
        new("h264_amf", "h264", Hardware: true, "nv12"),
        new("h264_mf", "h264", Hardware: false, "nv12"),
        new("hevc_nvenc", "hevc", Hardware: true, "nv12"),
        new("hevc_qsv", "hevc", Hardware: true, "nv12"),
        new("hevc_amf", "hevc", Hardware: true, "nv12"),
        new("hevc_mf", "hevc", Hardware: false, "nv12"),
    ];
}
