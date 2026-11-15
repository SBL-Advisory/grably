using Grably.App;

namespace Grably.Tests.App;

public class LaunchOptionsTests
{
    [Fact]
    public void No_arguments_opens_normally() =>
        Assert.Equal(new LaunchOptions(), LaunchOptions.Parse([]));

    [Theory]
    [InlineData("--background")]
    [InlineData("--BACKGROUND")]
    public void Background_flag_is_recognised(string flag) =>
        Assert.True(LaunchOptions.Parse([flag]).Background);

    [Fact]
    public void Unknown_and_dangling_arguments_are_ignored()
    {
        var options = LaunchOptions.Parse(["--nope", "x", "--out"]);

        Assert.False(options.Background);
        Assert.Null(options.Snapshot);
    }

#if DEBUG
    [Fact]
    public void Snapshot_request_is_parsed_in_debug_builds()
    {
        var options = LaunchOptions.Parse(["--ui-snapshot", "Library", "--out", "x.png", "--theme", "dark"]);

        Assert.Equal(new SnapshotRequest("Library", "x.png", "dark"), options.Snapshot);
    }

    [Fact]
    public void Snapshot_output_defaults_to_the_window_name() =>
        Assert.Equal("Library.png", LaunchOptions.Parse(["--ui-snapshot", "Library"]).Snapshot?.OutputPath);
#else
    [Fact]
    public void Snapshot_option_is_ignored_in_release_builds() =>
        Assert.Null(LaunchOptions.Parse(["--ui-snapshot", "Library"]).Snapshot);
#endif
}
