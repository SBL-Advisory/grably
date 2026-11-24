using Grably.Platform.Shell;
using Microsoft.Win32;

namespace Grably.Tests.Platform;

/// <summary>Uses a scratch key under HKCU, never the real Run key.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
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
}
