using Microsoft.Win32;

namespace Grably.Platform.Shell;

/// <summary>Starting with Windows, through the per-user Run key (no admin rights, no scheduled task).</summary>
public sealed class StartupRegistration
{
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _valueName;
    private readonly string _runKey;

    public StartupRegistration()
        : this("Grably", WindowsRunKey)
    {
    }

    /// <summary>For tests: a value under another HKCU key, so the real Run key is never touched.</summary>
    internal StartupRegistration(string valueName, string runKey)
    {
        _valueName = valueName;
        _runKey = runKey;
    }

    /// <summary>The command Windows runs at sign-in, or null when Grably does not start with Windows.</summary>
    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey);
            return key?.GetValue(_valueName) as string;
        }
    }

    public bool IsEnabled => Command is not null;

    /// <summary>Starts <paramref name="executable"/> in the background at sign-in, or stops doing so.</summary>
    public void Set(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKey, writable: true);
        if (enabled)
        {
            key.SetValue(_valueName, $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }
}
