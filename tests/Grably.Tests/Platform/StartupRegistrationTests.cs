using Grably.Platform.Shell;
using Microsoft.Win32;

namespace Grably.Tests.Platform;

/// <summary>Uses a scratch key under HKCU, never the real Run key.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private const string Installed = @"C:\Users\me\AppData\Local\Grably\current\Grably.exe";
    private const string Portable = @"D:\Tools\Grably\Grably.exe";

    private readonly string _key = $@"Software\Grably.Tests\{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    [Fact]
    public void Enabling_writes_a_quoted_background_command_and_disabling_removes_it()
    {
        var startup = new StartupRegistration("Grably", _key);
        Assert.False(startup.IsEnabled);

        startup.Set(true, @"C:\Program Files\Grably\Grably.exe");

        Assert.True(startup.IsEnabled);
        Assert.Equal("\"C:\\Program Files\\Grably\\Grably.exe\" --background", startup.Command);

        startup.Set(false, @"C:\Program Files\Grably\Grably.exe");
        Assert.False(startup.IsEnabled);
        startup.Set(false, @"C:\Program Files\Grably\Grably.exe"); // already off: no error
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Following_never_turns_starting_with_Windows_on(bool rewrite)
    {
        var startup = new StartupRegistration("Grably", _key);

        var (state, previous) = startup.Follow(Portable, rewrite);

        Assert.Equal(StartupEntryState.Off, state);
        Assert.Null(previous);
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void An_entry_for_this_executable_is_left_alone_whatever_its_case()
    {
        var startup = new StartupRegistration("Grably", _key);
        startup.Set(true, Installed.ToUpperInvariant());
        var before = startup.Command;

        var (state, _) = startup.Follow(Installed, rewrite: true);

        Assert.Equal(StartupEntryState.ThisCopy, state);
        Assert.Equal(before, startup.Command);
    }

    [Fact]
    public void A_debug_build_only_reports_an_entry_for_another_copy()
    {
        var startup = new StartupRegistration("Grably", _key);
        startup.Set(true, Installed);
        var before = startup.Command;

        var (state, previous) = startup.Follow(Portable, rewrite: false);

        Assert.Equal(StartupEntryState.OtherCopy, state);
        Assert.Equal(Installed, previous);
        Assert.Equal(before, startup.Command);
    }

    [Fact]
    public void An_unquoted_entry_for_this_executable_is_left_alone()
    {
        var startup = new StartupRegistration("Grably", _key);
        Registry.CurrentUser.CreateSubKey(_key).SetValue("Grably", $"{Installed} --background");

        var (state, _) = startup.Follow(Installed, rewrite: true);

        Assert.Equal(StartupEntryState.ThisCopy, state);
        Assert.Equal($"{Installed} --background", startup.Command);
    }

    [Fact]
    public void A_malformed_entry_is_replaced_by_a_release_build()
    {
        var startup = new StartupRegistration("Grably", _key);
        Registry.CurrentUser.CreateSubKey(_key).SetValue("Grably", "--background");

        var (state, previous) = startup.Follow(Portable, rewrite: true);

        Assert.Equal(StartupEntryState.Rewritten, state);
        Assert.Equal("--background", previous);
        Assert.Equal($"\"{Portable}\" --background", startup.Command);
    }

    [Fact]
    public void Uninstalling_removes_only_an_entry_for_this_executable()
    {
        var startup = new StartupRegistration("Grably", _key);
        Assert.False(startup.RemoveFor(Installed)); // off: nothing to do

        startup.Set(true, Portable);
        Assert.False(startup.RemoveFor(Installed));
        Assert.True(startup.IsEnabled); // the portable copy the user runs now keeps starting

        startup.Set(true, Installed);
        Assert.True(startup.RemoveFor(Installed.ToLowerInvariant()));
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void A_release_build_points_an_entry_for_another_copy_at_itself()
    {
        var startup = new StartupRegistration("Grably", _key);
        startup.Set(true, Installed);

        var (state, previous) = startup.Follow(Portable, rewrite: true);

        Assert.Equal(StartupEntryState.Rewritten, state);
        Assert.Equal(Installed, previous);
        Assert.Equal($"\"{Portable}\" --background", startup.Command);
    }
}
