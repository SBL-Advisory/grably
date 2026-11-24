using Grably.Platform.Shell;

namespace Grably.Tests.Platform;

public class ExecutableInfoTests
{
    [Fact]
    public void A_running_process_is_found_by_its_id()
    {
        var info = ExecutableInfo.ForProcess(Environment.ProcessId);

        Assert.NotNull(info);
        Assert.Equal(Environment.ProcessPath, info.Path, ignoreCase: true);
    }

    [Fact]
    public void A_missing_file_has_no_version()
    {
        var info = ExecutableInfo.ForFile(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe"));

        Assert.Null(info.Version);
        Assert.Equal("Grably", info.DisplayName);
    }

    [Fact]
    public void The_version_of_a_file_has_no_source_control_suffix()
    {
        var info = ExecutableInfo.ForFile(typeof(ExecutableInfo).Assembly.Location);

        Assert.Equal(Grably.Core.AppInfo.Version, info.Version);
        Assert.Equal($"Grably {Grably.Core.AppInfo.Version}", info.DisplayName);
    }
}
