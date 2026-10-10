using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentivity.Domain.Arguments;

/// <summary>
/// Represents a reference to a single image.
/// </summary>
public sealed class ImageRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("local_path")]
    public string? LocalPath { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("file_size_bytes")]
    public long? FileSizeBytes { get; set; }

    [JsonPropertyName("desired_size")]
    public string? DesiredSize { get; set; }

    [JsonPropertyName("cached_at")]
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;

    public static ImageRef FromUrl(string url, string? desiredSize = null) =>
        new()
        {
            Url = url,
            FileName = Path.GetFileName(url),
            MimeType = GuessMimeType(url),
            DesiredSize = desiredSize,
        };

    public static ImageRef FromLocalPath(string localPath, string? fileName = null, string? mimeType = null) =>
        new()
        {
            LocalPath = localPath,
            FileName = fileName ?? Path.GetFileName(localPath),
            MimeType = mimeType ?? GuessMimeType(localPath),
        };

    private static string GuessMimeType(string path) =>
        Path.GetExtension(path)?.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
}

/// <summary>
/// Represents a collection of image references.
/// </summary>
public sealed class WorkflowImagesRef
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = false };

    [JsonPropertyName("images")]
    public List<ImageRef> Images { get; set; } = [];

    public static WorkflowImagesRef Empty => new();

    public static WorkflowImagesRef Single(ImageRef image) => new() { Images = [image] };

    public static WorkflowImagesRef FromImages(IEnumerable<ImageRef> images) => new() { Images = images.ToList() };

    [JsonIgnore]
    public ImageRef? First => Images.Count > 0 ? Images[0] : null;

    [JsonIgnore]
    public int Count => Images.Count;

    [JsonIgnore]
    public bool IsEmpty => Images.Count == 0;

    public void Add(ImageRef image) => Images.Add(image);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static WorkflowImagesRef FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;
        try
        {
            return JsonSerializer.Deserialize<WorkflowImagesRef>(json, JsonOptions) ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public static WorkflowImagesRef FromJsonElement(JsonElement element)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowImagesRef>(element.GetRawText(), JsonOptions) ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }
}
