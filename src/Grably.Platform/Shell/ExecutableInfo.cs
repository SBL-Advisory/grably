using System.Diagnostics;
using Grably.Core;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Grably.Platform.Shell;

/// <summary>A Grably.exe and its version, to tell copies apart (a portable copy, a development build, an installed one).</summary>
/// <param name="Path">The full path of the executable.</param>
/// <param name="Version">The product version without the source-control suffix, or null when it cannot be read.</param>
public sealed record ExecutableInfo(string Path, string? Version)
{
    /// <summary>
    /// This process, with its path read the same way as another Grably's (<see cref="ForProcess"/>), so the same file
    /// is spelled the same way when the two are compared.
    /// </summary>
    public static ExecutableInfo Current { get; } =
        new(PathOfProcess(Environment.ProcessId) ?? Environment.ProcessPath ?? string.Empty, AppInfo.Version);

    /// <summary>"Grably 1.0.1", or just "Grably" when the version is unknown.</summary>
    public string DisplayName => Version is null ? AppInfo.Name : $"{AppInfo.Name} {Version}";

    /// <summary>The folder the executable is in.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? Path;

    /// <summary>The executable of a running process, or null when it has exited or cannot be queried.</summary>
    public static ExecutableInfo? ForProcess(int processId) => PathOfProcess(processId) is { } path ? ForFile(path) : null;

    private static string? PathOfProcess(int processId)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process.IsInvalid)
        {
            return null;
        }

        var buffer = new char[32_768];
        var length = (uint)buffer.Length;
        return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length)
            ? new string(buffer, 0, (int)length)
            : null;
    }

    /// <summary>An executable on disk; the version is null when the file is missing or has no version.</summary>
    public static ExecutableInfo ForFile(string path)
    {
        string? version = null;
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductVersion is { Length: > 0 } product)
            {
                version = AppInfo.WithoutBuildMetadata(product);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The version only labels the copy in messages and log lines.
        }

        return new ExecutableInfo(path, version);
    }
}
