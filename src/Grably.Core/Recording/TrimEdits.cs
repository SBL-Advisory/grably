using System.Security.Cryptography;
using System.Text;

namespace Grably.Core.Recording;

/// <summary>
/// Keeps each video's edit list between sessions, so trimming can be picked up again: one small JSON file per video
/// in the trims folder, named after the video's path. The video itself is never touched.
/// </summary>
public static class TrimEdits
{
    /// <summary>A saved list is only used while the video still has the length it was made for.</summary>
    private static readonly TimeSpan LengthTolerance = TimeSpan.FromMilliseconds(50);

    public static string PathFor(string folder, string video)
    {
        var key = Encoding.UTF8.GetBytes(Path.GetFullPath(video).ToUpperInvariant());
        return Path.Combine(folder, Convert.ToHexStringLower(SHA256.HashData(key))[..32] + ".json");
    }

    /// <summary>The saved edits of <paramref name="video"/>, or null when there are none or they no longer fit it.</summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static EditList? Load(string folder, string video, TimeSpan duration)
    {
        var path = PathFor(folder, video);
        if (!File.Exists(path))
        {
            return null;
        }

        var edits = EditList.FromJson(File.ReadAllText(path));
        return edits is not null && edits.Kept.Count > 0 && (edits.SourceDuration - duration).Duration() <= LengthTolerance ? edits : null;
    }

    /// <summary>Saves the edits; an untouched video needs no file, so any old one is removed.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void Save(string folder, string video, EditList edits)
    {
        var path = PathFor(folder, video);
        if (edits.IsWhole)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(folder);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, edits.ToJson());
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The video was renamed or moved: its edits follow it.</summary>
    /// <exception cref="IOException">The file could not be moved.</exception>
    public static void Move(string folder, string from, string to)
    {
        var source = PathFor(folder, from);
        if (File.Exists(source))
        {
            File.Move(source, PathFor(folder, to), overwrite: true);
        }
    }

    /// <exception cref="IOException">The file could not be deleted.</exception>
    public static void Delete(string folder, string video) => File.Delete(PathFor(folder, video));
}
