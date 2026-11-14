namespace Grably.Core.Capture;

/// <summary>File names for captures: Grably_2026-11-14_15-30-05.png, with _2, _3... when that name is taken.</summary>
public static class CaptureNaming
{
    public static string BaseName(DateTime timestamp) => $"Grably_{timestamp:yyyy-MM-dd_HH-mm-ss}";

    /// <param name="folder">Target folder.</param>
    /// <param name="timestamp">Capture time (local).</param>
    /// <param name="extension">Extension with or without the dot, e.g. "png".</param>
    /// <param name="exists">File existence check; defaults to <see cref="File.Exists"/>.</param>
    public static string UniquePath(string folder, DateTime timestamp, string extension, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var name = BaseName(timestamp);
        var path = Path.Combine(folder, name + ext);
        for (var n = 2; exists(path); n++)
        {
            path = Path.Combine(folder, $"{name}_{n}{ext}");
        }

        return path;
    }
}
