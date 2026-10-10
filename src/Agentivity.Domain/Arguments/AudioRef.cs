using System.Text.Json.Serialization;

namespace Agentivity.Domain.Arguments;

/// <summary>
/// Represents a reference to a single audio file — the audio counterpart of <see cref="ImageRef"/>.
/// </summary>
public sealed class AudioRef
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

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; set; }

    [JsonPropertyName("file_size_bytes")]
    public long? FileSizeBytes { get; set; }

    [JsonPropertyName("cached_at")]
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;
}
