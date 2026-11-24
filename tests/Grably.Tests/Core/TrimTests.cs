using Grably.Core.Capture;
using Grably.Core.Recording;

namespace Grably.Tests.Core;

public class TrimTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Moving_a_handle_inwards_trims_and_outwards_brings_the_end_back_keeping_the_cuts()
    {
        var edits = EditList.Whole(S(60)).Trim(S(10), S(50)).Cut(new TimeRange(S(20), S(30)));

        var wider = edits.WithEnds(S(2), S(55));
        var narrower = edits.WithEnds(S(15), S(45));

        Assert.Equal([new TimeRange(S(2), S(20)), new TimeRange(S(30), S(55))], wider.Kept);
        Assert.Equal([new TimeRange(S(15), S(20)), new TimeRange(S(30), S(45))], narrower.Kept);
        Assert.Equal((S(15), S(45)), (narrower.Start, narrower.End));
    }

    [Fact]
    public void Handles_moved_past_a_cut_drop_the_parts_beyond_them()
    {
        var edits = EditList.Whole(S(60)).Cut(new TimeRange(S(20), S(30)));

        Assert.Equal([new TimeRange(S(35), S(60))], edits.WithEnds(S(35), S(60)).Kept);
        Assert.Equal([new TimeRange(S(0), S(15))], edits.WithEnds(S(0), S(15)).Kept);
    }

    [Theory]
    [InlineData(0.0, 30, 0)]
    [InlineData(0.0333, 30, 0)]
    [InlineData(1.0 / 30, 30, 1)]
    [InlineData(2.0 / 30, 30, 2)]
    [InlineData(8.2, 30, 246)]
    [InlineData(1.0, 60, 60)]
    public void A_time_is_on_the_frame_that_started_last(double seconds, double fps, int frame) =>
        Assert.Equal(frame, VideoFrames.Index(S(seconds), fps));

    [Fact]
    public void Frame_starts_round_trip_and_seeks_land_inside_the_frame_before_it_starts()
    {
        for (var index = 0; index < 1000; index++)
        {
            Assert.Equal(index, VideoFrames.Index(VideoFrames.Start(index, 30), 30));
            Assert.Equal(index, VideoFrames.Index(VideoFrames.Snap(VideoFrames.Start(index, 30) + TimeSpan.FromMilliseconds(10), 30), 30));
            var seek = VideoFrames.SeekTime(index, 30);
            Assert.True(seek <= VideoFrames.Start(index, 30));
            Assert.True(index == 0 || seek > VideoFrames.Start(index - 1, 30));
        }
    }

    [Theory]
    [InlineData(0.0, "0:00.00")]
    [InlineData(8.2, "0:08.20")]
    [InlineData(65.999, "1:05.99")]
    [InlineData(3725.5, "1:02:05.50")]
    public void Times_read_as_minutes_seconds_and_hundredths(double seconds, string text) =>
        Assert.Equal(text, VideoFrames.Format(S(seconds)));

    [Fact]
    public void Saved_edits_come_back_follow_a_rename_and_are_dropped_when_the_video_changed()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"grably-trims-{Guid.NewGuid():N}");
        try
        {
            var video = @"C:\Videos\Grably\clip.mp4";
            var edits = EditList.Whole(S(60)).Cut(new TimeRange(S(10), S(20)));
            TrimEdits.Save(folder, video, edits);

            Assert.Equal(edits, TrimEdits.Load(folder, video, S(60.01)));
            Assert.Equal(edits, TrimEdits.Load(folder, @"c:\videos\grably\CLIP.mp4", S(60)));
            Assert.Null(TrimEdits.Load(folder, video, S(45))); // a different video under the same name

            TrimEdits.Move(folder, video, @"C:\Videos\Grably\renamed.mp4");
            Assert.Null(TrimEdits.Load(folder, video, S(60)));
            Assert.Equal(edits, TrimEdits.Load(folder, @"C:\Videos\Grably\renamed.mp4", S(60)));

            TrimEdits.Save(folder, @"C:\Videos\Grably\renamed.mp4", EditList.Whole(S(60))); // untouched again: no file
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Exports_go_next_to_the_recording_with_a_name_that_is_free()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Videos\Grably_2026-11-23_10-24-02_edited.mp4",
            @"C:\Videos\Grably_2026-11-23_10-24-02_edited_2.mp4",
        };

        Assert.Equal(@"C:\Videos\Grably_2026-11-23_10-24-02_edited_3.mp4", CaptureNaming.ExportPath(@"C:\Videos\Grably_2026-11-23_10-24-02.mp4", "mp4", taken.Contains));
        Assert.Equal(@"C:\Videos\Grably_2026-11-23_10-24-02_edited.gif", CaptureNaming.ExportPath(@"C:\Videos\Grably_2026-11-23_10-24-02.mp4", "gif", taken.Contains));
    }

    [Fact]
    public void Preview_frames_are_scaled_bitmaps_and_long_videos_use_keyframes_for_thumbnails()
    {
        var frame = FfmpegArguments.FrameAt("in.mp4", S(1.5), maxWidth: 1600);
        var full = FfmpegArguments.FrameAt("in.mp4", S(1.5));

        Assert.Contains("scale='min(1600,iw)':-2:flags=bicubic", frame);
        Assert.DoesNotContain("-vf", full);
        Assert.Equal(["bmp", "-"], full.TakeLast(2));
        Assert.DoesNotContain("-skip_frame", FfmpegArguments.ThumbnailStrip("in.mp4", S(90), 40, 90, "t.png"));
        Assert.Contains("-skip_frame", FfmpegArguments.ThumbnailStrip("in.mp4", S(600), 40, 90, "t.png"));
    }
}
