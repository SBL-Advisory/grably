namespace Grably.App;

/// <summary>Command-line options of Grably.exe.</summary>
public sealed record LaunchOptions
{
    /// <summary>Start without opening a window (used when Grably starts with Windows).</summary>
    public bool Background { get; init; }

    /// <summary>Debug builds only: render a window to a PNG and exit.</summary>
    public SnapshotRequest? Snapshot { get; init; }

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var background = false;
        string? window = null, output = null, theme = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--background":
                    background = true;
                    break;
                case "--ui-snapshot" when i + 1 < args.Count:
                    window = args[++i];
                    break;
                case "--out" when i + 1 < args.Count:
                    output = args[++i];
                    break;
                case "--theme" when i + 1 < args.Count:
                    theme = args[++i];
                    break;
            }
        }

#if DEBUG
        var snapshot = window is null ? null : new SnapshotRequest(window, output ?? $"{window}.png", theme);
#else
        // Release builds have no snapshot support; ignore the option rather than bypass the single-instance guard.
        SnapshotRequest? snapshot = null;
#endif
        return new LaunchOptions { Background = background, Snapshot = snapshot };
    }
}

/// <param name="Window">Name of the window to render, e.g. "Library".</param>
/// <param name="OutputPath">PNG file to write.</param>
/// <param name="Theme">"light" or "dark"; null follows the settings.</param>
public sealed record SnapshotRequest(string Window, string OutputPath, string? Theme);
