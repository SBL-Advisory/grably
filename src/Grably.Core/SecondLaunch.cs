namespace Grably.Core;

/// <summary>What a launch does when another Grably already runs for this user.</summary>
public enum SecondLaunchAction
{
    /// <summary>Hand the launch to the running Grably, as every version has always done.</summary>
    HandOver,

    /// <summary>A different Grably.exe is running: ask whether to close it and start this one instead.</summary>
    OfferToReplace,
}

/// <summary>
/// Decides between handing a launch to the running Grably and offering to replace it. A copy from another folder (a
/// newer build, a portable copy, a moved folder) would otherwise take over every launch without anyone noticing.
/// </summary>
public static class SecondLaunch
{
    /// <summary>The first Grably that a launch of a different Grably.exe can ask to close.</summary>
    public static readonly Version FirstToAcceptCloseRequests = new(1, 0, 2);

    /// <summary>
    /// Whether a Grably of this version can be asked to close. Unknown versions count as older: a copy that predates
    /// this is told apart from one that is only busy answering another launch.
    /// </summary>
    public static bool AcceptsCloseRequests(string? version) =>
        version is not null
        && Version.TryParse(version.Split('-', '+')[0], out var parsed)
        && parsed >= FirstToAcceptCloseRequests;

    /// <param name="ownExecutable">This launch's Grably.exe.</param>
    /// <param name="runningExecutable">The running Grably's executable, or null when it could not be found out.</param>
    /// <param name="background">A <c>--background</c> launch (at sign-in): never asks, so nothing pops up.</param>
    public static SecondLaunchAction Decide(string ownExecutable, string? runningExecutable, bool background) =>
        background || string.IsNullOrWhiteSpace(runningExecutable) || IsSameExecutable(ownExecutable, runningExecutable)
            ? SecondLaunchAction.HandOver
            : SecondLaunchAction.OfferToReplace;

    /// <summary>Compares full paths, ignoring case (Windows paths are case-insensitive).</summary>
    public static bool IsSameExecutable(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
