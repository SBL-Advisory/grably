using System.IO.Compression;
using System.Text;

namespace Grably.Core.Annotations;

/// <summary>Where a project's flattened image lives, so opening the project can save back to it.</summary>
public sealed record ProjectInfo(string? ImagePath, DateTime CreatedUtc);

/// <summary>
/// A re-editable screenshot: a zip (".grably") holding the untouched original image and the annotation document.
/// The flattened PNG the user sees is a separate file; this is what makes it editable again.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".grably";
    private const string OriginalEntry = "original.png";
    private const string DocumentEntry = "document.json";
    private const string InfoEntry = "project.json";

    /// <summary>Writes the project atomically (temporary file, then replace).</summary>
    public static void Save(string path, byte[] originalPng, AnnotationDocument document, ProjectInfo? info = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.Delete(temp); // a leftover from a crash would make creating the zip fail
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                // PNG is already compressed; storing it avoids burning time compressing it again.
                using (var image = zip.CreateEntry(OriginalEntry, CompressionLevel.NoCompression).Open())
                {
                    image.Write(originalPng);
                }

                using (var json = new StreamWriter(zip.CreateEntry(DocumentEntry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                {
                    json.Write(AnnotationJson.Serialize(document));
                }

                if (info is not null)
                {
                    using var meta = new StreamWriter(zip.CreateEntry(InfoEntry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                    meta.Write(System.Text.Json.JsonSerializer.Serialize(info, AnnotationJson.Options));
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // Best effort.
            }

            throw;
        }
    }

    /// <summary>Replaces only the document in an existing project, keeping the original image and info as they are.</summary>
    public static void SaveDocument(string path, AnnotationDocument document)
    {
        var project = Load(path);
        Save(path, project.OriginalPng, document, project.Info);
    }

    /// <summary>Reads only the info, without the image or the document (cheap, for scanning many projects).</summary>
    public static ProjectInfo? LoadInfo(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.GetEntry(InfoEntry) is not { } entry)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return System.Text.Json.JsonSerializer.Deserialize<ProjectInfo>(reader.ReadToEnd(), AnnotationJson.Options);
    }

    /// <summary>Replaces only the info (the flattened image's path), for example after that image was renamed.</summary>
    public static void SaveInfo(string path, ProjectInfo info)
    {
        var project = Load(path);
        Save(path, project.OriginalPng, project.Document, info);
    }

    public static (byte[] OriginalPng, AnnotationDocument Document, ProjectInfo? Info) Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var imageEntry = zip.GetEntry(OriginalEntry) ?? throw new InvalidDataException($"{path} has no {OriginalEntry}.");
        var documentEntry = zip.GetEntry(DocumentEntry) ?? throw new InvalidDataException($"{path} has no {DocumentEntry}.");

        using var imageStream = imageEntry.Open();
        using var buffer = new MemoryStream();
        imageStream.CopyTo(buffer);

        string documentJson;
        using (var reader = new StreamReader(documentEntry.Open(), Encoding.UTF8))
        {
            documentJson = reader.ReadToEnd();
        }

        ProjectInfo? info = null;
        if (zip.GetEntry(InfoEntry) is { } infoEntry)
        {
            using var reader = new StreamReader(infoEntry.Open(), Encoding.UTF8);
            info = System.Text.Json.JsonSerializer.Deserialize<ProjectInfo>(reader.ReadToEnd(), AnnotationJson.Options);
        }

        return (buffer.ToArray(), AnnotationJson.Deserialize(documentJson), info);
    }
}
