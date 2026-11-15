using System.Reflection;

namespace Grably.Core;

public static class AppInfo
{
    public const string Name = "Grably";

    /// <summary>Identifies the app to Windows objects such as the single-instance mutex.</summary>
    public const string Id = "Grably";

    /// <summary>The product version from Directory.Build.props, without the source-control suffix.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}
