using Grably.Core;

namespace Grably.Tests.Core;

public class SecondLaunchTests
{
    private const string Installed = @"C:\Users\me\AppData\Local\Grably\current\Grably.exe";

    [Theory]
    [InlineData(Installed)]
    [InlineData(@"c:\users\ME\appdata\local\GRABLY\current\grably.EXE")]
    [InlineData("C:/Users/me/AppData/Local/Grably/current/Grably.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Grably\app\..\current\Grably.exe")]
    public void The_same_executable_hands_over(string running) =>
        Assert.Equal(SecondLaunchAction.HandOver, SecondLaunch.Decide(Installed, running, background: false));

    [Theory]
    [InlineData(@"D:\repos\grably\artifacts\publish\Grably.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Grably\current\Grably2.exe")]
    [InlineData(@"C:\Users\me\Downloads\Grably-portable\Grably.exe")]
    public void A_different_executable_offers_to_replace_it(string running) =>
        Assert.Equal(SecondLaunchAction.OfferToReplace, SecondLaunch.Decide(Installed, running, background: false));

    [Fact]
    public void A_background_launch_always_hands_over() =>
        Assert.Equal(
            SecondLaunchAction.HandOver,
            SecondLaunch.Decide(Installed, @"D:\repos\grably\artifacts\publish\Grably.exe", background: true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unknown_running_executable_hands_over(string? running) =>
        Assert.Equal(SecondLaunchAction.HandOver, SecondLaunch.Decide(Installed, running, background: false));

    [Theory]
    [InlineData("1.0.2", true)]
    [InlineData("1.0.3", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.1002", true)] // the test update install.ps1 packs for 1.0.2
    [InlineData("2.0.0-beta.1", true)]
    [InlineData("1.0.1", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.1.1010", false)]
    [InlineData("not a version", false)]
    [InlineData(null, false)]
    public void Only_1_0_2_and_later_accept_close_requests(string? version, bool accepts) =>
        Assert.Equal(accepts, SecondLaunch.AcceptsCloseRequests(version));

    [Theory]
    [InlineData("\"C:\\Program Files\\Grably\\Grably.exe\" --background", @"C:\Program Files\Grably\Grably.exe")]
    [InlineData(@"C:\my.exes\Grably.exe --background", @"C:\my.exes\Grably.exe")]
    [InlineData(@"C:\Tools\Grably.exe", @"C:\Tools\Grably.exe")]
    [InlineData("  \"C:\\Grably\\Grably.exe\"", @"C:\Grably\Grably.exe")]
    [InlineData(@"C:\Grably\Grably.exe --background", @"C:\Grably\Grably.exe")]
    [InlineData(@"C:\My Apps\Grably\GRABLY.EXE --background", @"C:\My Apps\Grably\GRABLY.EXE")]
    [InlineData("", null)]
    [InlineData("\"\" --background", null)]
    [InlineData("--background", null)]
    public void The_executable_is_read_from_a_sign_in_command(string command, string? expected) =>
        Assert.Equal(expected, StartupCommand.Executable(command));

    [Fact]
    public void A_sign_in_command_round_trips() =>
        Assert.Equal(
            @"C:\Program Files\Grably\Grably.exe",
            StartupCommand.Executable(StartupCommand.For(@"C:\Program Files\Grably\Grably.exe")));

    [Theory]
    [InlineData("1.0.1+3f2a9c0d", "1.0.1")]
    [InlineData("0.1.10", "0.1.10")]
    public void Versions_lose_their_source_control_suffix(string informational, string expected) =>
        Assert.Equal(expected, AppInfo.WithoutBuildMetadata(informational));
}
