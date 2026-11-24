using System.Text.Json;
using System.Text.Json.Serialization;
using Grably.Core.Geometry;

namespace Grably.Core.Annotations;

/// <summary>A screenshot's annotations: everything needed to re-render or keep editing it over the original image.</summary>
public sealed class AnnotationDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Size of the original image in pixels.</summary>
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Non-destructive crop in image pixels; null shows the whole image.</summary>
    public PixelRect? Crop { get; set; }

    /// <summary>Bottom to top.</summary>
    public List<AnnotationItem> Items { get; set; } = [];

    /// <summary>The part of the image that is shown and exported.</summary>
    [JsonIgnore]
    public PixelRect ViewArea => Crop is { IsEmpty: false } crop ? crop : new PixelRect(0, 0, Width, Height);

    public static AnnotationDocument ForImage(int width, int height) => new() { Width = width, Height = height };

    /// <summary>The number the next step badge should show.</summary>
    public int NextStepNumber() => Items.OfType<StepItem>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    public AnnotationItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);
}

/// <summary>JSON for documents and items: camelCase, enums as names, colours as hex.</summary>
public static class AnnotationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(AnnotationDocument document) => JsonSerializer.Serialize(document, Options);

    public static AnnotationDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<AnnotationDocument>(json, Options)
            ?? throw new JsonException("The annotation document is empty.");
        if (document.Version > AnnotationDocument.CurrentVersion)
        {
            throw new NotSupportedException(
                $"This document was saved by a newer Grably (format {document.Version}); update Grably to open it.");
        }

        document.Items.RemoveAll(i => i is null);
        return document;
    }

    public static string Serialize(AnnotationItem item) => JsonSerializer.Serialize(item, Options);

    public static AnnotationItem DeserializeItem(string json) =>
        JsonSerializer.Deserialize<AnnotationItem>(json, Options) ?? throw new JsonException("The annotation is empty.");

    /// <summary>A deep copy with a new id (for duplicate and paste).</summary>
    public static AnnotationItem Clone(AnnotationItem item)
    {
        var copy = DeserializeItem(Serialize(item));
        copy.Id = Guid.NewGuid().ToString("N");
        return copy;
    }
}
