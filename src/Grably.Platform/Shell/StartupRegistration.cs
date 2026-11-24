using Grably.Core;
using Microsoft.Win32;

namespace Grably.Platform.Shell;

/// <summary>What <see cref="StartupRegistration.Follow"/> found and did.</summary>
public enum StartupEntryState
{
    /// <summary>Grably does not start with Windows; nothing was written.</summary>
    Off,

    /// <summary>The entry already starts this executable.</summary>
    ThisCopy,

    /// <summary>The entry starts another executable and was left as it is.</summary>
    OtherCopy,

    /// <summary>The entry started another executable and now starts this one.</summary>
    Rewritten,
}

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
            key.SetValue(_valueName, StartupCommand.For(executable), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Stops starting <paramref name="executable"/> at sign-in (uninstalling it). An entry for another copy stays: that
    /// copy may be the one the user runs now. True when the entry was removed.
    /// </summary>
    public bool RemoveFor(string executable)
    {
        if (StartupCommand.Executable(Command) is not { } named || !SecondLaunch.IsSameExecutable(named, executable))
        {
            return false;
        }

        Set(false, executable);
        return true;
    }

    /// <summary>
    /// When Grably starts with Windows but the entry names another executable (another build folder, a portable or
    /// moved copy), points it at <paramref name="executable"/> if <paramref name="rewrite"/> is set, so the copy the
    /// user last ran is the one that starts at sign-in. Never turns starting with Windows on.
    /// </summary>
    /// <returns>What was found, and the executable the entry named before (null when off).</returns>
    public (StartupEntryState State, string? Previous) Follow(string executable, bool rewrite)
    {
        if (Command is not { } command)
        {
            return (StartupEntryState.Off, null);
        }

        var named = StartupCommand.Executable(command);
        var previous = named ?? command;
        if (named is not null && SecondLaunch.IsSameExecutable(named, executable))
        {
            return (StartupEntryState.ThisCopy, previous);
        }

        if (!rewrite)
        {
            return (StartupEntryState.OtherCopy, previous);
        }

        Set(true, executable);
        return (StartupEntryState.Rewritten, previous);
    }
}
