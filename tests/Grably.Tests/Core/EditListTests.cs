using Grably.Core.Recording;
using Grably.Core.Settings;

namespace Grably.Tests.Core;

public class EditListTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Trimming_and_cutting_keep_the_right_parts_and_length()
    {
        var edits = EditList.Whole(S(60)).Trim(S(5), S(50)).Cut(new TimeRange(S(20), S(30)));

        Assert.Equal([new TimeRange(S(5), S(20)), new TimeRange(S(30), S(50))], edits.Kept);
        Assert.Equal(S(35), edits.Duration);
        Assert.False(edits.IsWhole);
        Assert.True(EditList.Whole(S(60)).IsWhole);
    }

    [Fact]
    public void Cuts_that_touch_or_overlap_merge_and_slivers_disappear()
    {
        var edits = EditList.Whole(S(10)).Cut(new TimeRange(S(2), S(4))).Cut(new TimeRange(S(3.5), S(6))).Cut(new TimeRange(S(6.004), S(9.997)));

        // 6.000-6.004 and 9.997-10 are shorter than EditList.Shortest, so only the first part is left.
        Assert.Equal([new TimeRange(S(0), S(2))], edits.Kept);
    }

    [Fact]
    public void Playback_skips_removed_parts_and_times_map_to_the_edited_video()
    {
        var edits = EditList.Whole(S(60)).Trim(S(5), S(50)).Cut(new TimeRange(S(20), S(30)));

        Assert.Equal(S(5), edits.Playable(S(1)));
        Assert.Equal(S(12), edits.Playable(S(12)));
        Assert.Equal(S(30), edits.Playable(S(25)));
        Assert.Null(edits.Playable(S(55)));
        Assert.Equal(S(15), edits.ToEdited(S(20)));
        Assert.Equal(S(15), edits.ToEdited(S(25))); // inside the cut: where the cut is
        Assert.Equal(S(25), edits.ToEdited(S(40)));
    }

    [Fact]
    public void Undo_and_redo_walk_the_versions_and_a_new_edit_drops_the_redo()
    {
        var history = new EditHistory(EditList.Whole(S(10)));
        history.Apply(history.Current.Trim(S(1), S(9)));
        history.Apply(history.Current.Cut(new TimeRange(S(4), S(5))));

        history.Undo();
        Assert.Equal(S(8), history.Current.Duration);
        Assert.True(history.CanRedo);
        history.Apply(history.Current.Trim(S(2), S(9)));
        Assert.False(history.CanRedo);
        history.Undo();
        history.Undo();
        Assert.True(history.Current.IsWhole);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void An_edit_list_survives_json_and_junk_reads_as_none()
    {
        var edits = EditList.Whole(S(60)).Cut(new TimeRange(S(10), S(20)));

        Assert.Equal(edits, EditList.FromJson(edits.ToJson()));
        Assert.Null(EditList.FromJson("{ nope"));
    }

    [Fact]
    public void Mp4_export_cuts_each_part_frame_accurately_and_joins_them_with_the_sound()
    {
        var edits = EditList.Whole(S(60)).Trim(S(5), S(50)).Cut(new TimeRange(S(20), S(30)));
        var qsv = VideoEncoders.Candidates.Single(e => e.Name == "h264_qsv");

        var args = FfmpegArguments.Export("in.mp4", edits, new ExportSettings(ExportFormat.Mp4), hasAudio: true, qsv, VideoQuality.High, 1280, 720, 30, "out.mp4");
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];

        // Pictures are cut half a frame early (between frames, whatever the rounding of their time stamps), sound exactly.
        Assert.Contains("[0:v]trim=start=4.983333:end=19.983333,setpts=PTS-STARTPTS[v0];", graph);
        Assert.Contains("[0:v]trim=start=29.983333:end=49.983333,setpts=PTS-STARTPTS[v1];", graph);
        Assert.Contains("[0:a]atrim=start=30:end=50,asetpts=PTS-STARTPTS[a1];", graph);
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1[cv][a];", graph);
        Assert.EndsWith("[cv]scale=1280:720:flags=lanczos,format=nv12[v]", graph);
        Assert.Contains("aac", args);
        Assert.Contains("+faststart", args);
        Assert.Equal(["-f", "mp4", "-y", "out.mp4"], args.TakeLast(4));
        Assert.Contains("pipe:1", args); // progress
    }

    [Fact]
    public void Gif_export_has_no_sound_and_makes_its_own_palette()
    {
        var edits = EditList.Whole(S(10)).Trim(S(1), S(4));

        var args = FfmpegArguments.Export(
            "in.mp4", edits, new ExportSettings(ExportFormat.Gif, GifFrameRate: 10, GifWidth: 640), hasAudio: true, null, VideoQuality.High, 640, 360, 30, "out.gif");
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];

        Assert.DoesNotContain("atrim", graph);
        Assert.Contains("concat=n=1:v=1:a=0[cv];", graph);
        Assert.Contains("[cv]fps=10,scale=640:-2:flags=lanczos,split[g0][g1];[g0]palettegen", graph);
        Assert.Contains("-loop", args);
        Assert.Contains("[0:v]trim=start=0.983333:end=3.983333", graph);
        Assert.Equal(["-f", "gif", "-y", "out.gif"], args.TakeLast(4));
    }
}
