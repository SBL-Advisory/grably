using System.Reflection;

namespace Grably.Core;

public static class AppInfo
{
    public const string Name = "Grably";

    /// <summary>Identifies the app to Windows objects such as the single-instance mutex.</summary>
    public const string Id = "Grably";

    /// <summary>The company credited as Grably's builder in Settings (About).</summary>
    public const string CreditName = "Strategies Beyond Limits";

    /// <summary>Where the credit links to.</summary>
    public const string CreditUrl = "https://www.sbladvisory.com";

    /// <summary>The product version from Directory.Build.props, without the source-control suffix.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}
