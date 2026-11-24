using Grably.Core.Recording;
using Grably.Core.Settings;

namespace Grably.Tests.Core;

public class RecordingCoreTests
{
    [Fact]
    public void On_time_frames_are_written_once_and_late_ones_catch_up_with_the_clock()
    {
        var pacer = new FramePacer(30);

        Assert.Equal(1, pacer.Take(TimeSpan.Zero)); // frame 0, on time
        Assert.Equal(TimeSpan.FromSeconds(1 / 30.0), pacer.NextDue);
        Assert.Equal(1, pacer.Take(TimeSpan.FromMilliseconds(34)));
        Assert.Equal(3, pacer.Take(TimeSpan.FromMilliseconds(135))); // frames 2, 3 and 4 were due by now: one frame, three times

        Assert.Equal(5, pacer.Frames);
        Assert.Equal(2, pacer.Repeats);
        Assert.Equal(TimeSpan.FromSeconds(5 / 30.0), pacer.Duration);
    }

    [Fact]
    public void Over_a_long_recording_the_frame_count_follows_the_clock_whatever_the_jitter()
    {
        var pacer = new FramePacer(60);
        var random = new Random(1);
        var t = 0.0;
        while (t < 600)
        {
            pacer.Take(TimeSpan.FromSeconds(t));
            t = pacer.NextDue.TotalSeconds + (random.NextDouble() * 0.05); // up to 50 ms late
        }

        Assert.InRange(pacer.Frames, 600 * 60, (600 * 60) + 4);
    }

    [Theory]
    [InlineData(3440, 1440, null, null, 3440, 1440)]
    [InlineData(801, 601, null, null, 800, 600)] // H.264 4:2:0 needs even sizes
    [InlineData(3840, 2160, 1080, null, 1920, 1080)]
    [InlineData(5120, 1440, null, 4096, 4096, 1152)]
    [InlineData(1, 1, null, null, 2, 2)]
    public void Output_size_fits_the_limits_and_is_even(int width, int height, int? maxHeight, int? maxWidth, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), OutputSize.For(width, height, maxHeight, maxWidth));

    [Fact]
    public void Nv12_input_is_described_as_bt709_and_goes_straight_to_a_hardware_encoder()
    {
        var qsv = VideoEncoders.Candidates.Single(e => e.Name == "h264_qsv");

        var args = FfmpegArguments.Encode(qsv, RawPixelFormat.Nv12, 1920, 1080, 30, VideoQuality.High, "out.mkv");

        AssertSequence(args, "-f", "rawvideo", "-pix_fmt", "nv12", "-video_size", "1920x1080", "-framerate", "30");
        Assert.True(args.ToList().IndexOf("-colorspace") < args.ToList().IndexOf("-i")); // the input is tagged too
        Assert.DoesNotContain("-vf", args);
        AssertSequence(args, "-c:v", "h264_qsv");
        AssertSequence(args, "-g", "30"); // a keyframe every second
        AssertSequence(args, "-f", "matroska", "-cluster_time_limit", "1000", "-flush_packets", "1", "-y", "out.mkv"); // a crash loses about a second
    }

    [Fact]
    public void Bgra_input_is_converted_with_the_bt709_matrix_not_ffmpegs_bt601_default()
    {
        var mediaFoundation = VideoEncoders.Candidates.Single(e => e.Name == "h264_mf");

        var args = FfmpegArguments.Encode(mediaFoundation, RawPixelFormat.Bgra, 1280, 720, 60, VideoQuality.Standard, "out.mkv");

        AssertSequence(args, "-pix_fmt", "bgra");
        AssertSequence(args, "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=nv12");
        AssertSequence(args, "-rate_control", "quality", "-quality", "60");
    }

    [Fact]
    public void Only_encoders_that_come_with_windows_or_the_driver_are_candidates()
    {
        var names = VideoEncoders.Candidates.Select(e => e.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("x264") || n.Contains("x265")); // GPL
        Assert.DoesNotContain(names, n => n.Contains("openh264")); // Cisco's patent licence covers only its own binaries
        Assert.Equal(["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"], names.Take(4));
        Assert.All(names.Skip(4), n => Assert.StartsWith("hevc_", n)); // HEVC only when no H.264 works
    }

    [Fact]
    public void Hevc_is_tagged_hvc1_in_mp4_so_apple_players_accept_it()
    {
        Assert.Contains("hvc1", FfmpegArguments.Remux("in.mkv", "out.mp4", "hevc"));
        Assert.DoesNotContain("-tag:v", FfmpegArguments.Remux("in.mkv", "out.mp4", "h264"));
        AssertSequence(FfmpegArguments.Remux("in.mkv", "out.mp4"), "-c", "copy", "-movflags", "+faststart", "-y", "out.mp4");
    }

    [Fact]
    public void A_session_survives_a_round_trip_and_a_damaged_file_reads_as_none()
    {
        var folder = Path.Combine(Path.GetTempPath(), "grably-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var session = new RecordingSession
            {
                Id = RecordingSession.NewId(new DateTime(2026, 11, 23, 9, 30, 0)),
                StartedUtc = new DateTime(2026, 11, 23, 8, 30, 0, DateTimeKind.Utc),
                Target = "Monitor 1",
                Width = 1920,
                Height = 1080,
                FrameRate = 30,
                Encoder = "h264_qsv",
            };

            session = session with { AudioTracks = ["system.f32", "microphone.f32"] };
            session.Save(folder);
            Assert.Equivalent(session, RecordingSession.Load(folder), strict: true);
            Assert.StartsWith("20261123-093000-", session.Id);
            Assert.Contains("\"state\": \"recording\"", File.ReadAllText(Path.Combine(folder, RecordingSession.FileName)));

            File.WriteAllText(Path.Combine(folder, RecordingSession.FileName), "{ not json");
            Assert.Null(RecordingSession.Load(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Audio_tracks_are_mixed_at_full_level_into_aac_while_the_video_is_copied()
    {
        IReadOnlyList<string> raw = ["-f", "f32le", "-ar", "48000", "-ac", "2"];

        var two = FfmpegArguments.Mux("video.mkv", [("system.f32", raw), ("microphone.f32", raw)], "out.mp4");
        var one = FfmpegArguments.Mux("video.mkv", [("microphone.f32", raw)], "out.mp4");

        AssertSequence(two, "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", "system.f32");
        AssertSequence(two, "-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.97,apad[a]");
        AssertSequence(two, "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac");
        Assert.Contains("-shortest", two); // the video decides the length
        AssertSequence(one, "-filter_complex", "[1:a]apad[a]");
        Assert.Equal(FfmpegArguments.Remux("video.mkv", "out.mp4"), FfmpegArguments.Mux("video.mkv", [], "out.mp4"));
    }

    private static void AssertSequence(IReadOnlyList<string> args, params string[] expected)
    {
        for (var i = 0; i + expected.Length <= args.Count; i++)
        {
            if (args.Skip(i).Take(expected.Length).SequenceEqual(expected))
            {
                return;
            }
        }

        Assert.Fail($"Expected '{string.Join(' ', expected)}' in: {string.Join(' ', args)}");
    }
}
