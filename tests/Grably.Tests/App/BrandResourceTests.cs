using System.Collections;
using System.Resources;

namespace Grably.Tests.App;

/// <summary>
/// ThemeController loads these by pack URI at startup, so a renamed or missing file would stop Grably from starting.
/// </summary>
public sealed class BrandResourceTests
{
    [Theory]
    [InlineData("assets/sbl/sbl-logo-color.png")]
    [InlineData("assets/sbl/sbl-logo-white.png")]
    [InlineData("assets/grably.ico")]
    [InlineData("assets/grably-app-icon.png")]
    public void Brand_images_are_embedded_in_Grably_exe(string resource)
    {
        Assert.Contains(resource, EmbeddedResourceNames());
    }

    private static HashSet<string> EmbeddedResourceNames()
    {
        var assembly = typeof(Grably.App.App).Assembly;
        using var stream = assembly.GetManifestResourceStream($"{assembly.GetName().Name}.g.resources")
            ?? throw new InvalidOperationException("Grably.exe has no WPF resources");
        using var reader = new ResourceReader(stream);
        return reader.Cast<DictionaryEntry>().Select(e => (string)e.Key).ToHashSet(StringComparer.Ordinal);
    }
}
