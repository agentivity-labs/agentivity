namespace Agentivity.Infrastructure.Options;

/// <summary>
/// Configuration options for the image cache service.
/// </summary>
public sealed class ImageCacheOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "ImageCache";

    /// <summary>
    /// Directory path for storing cached images.
    /// Can be absolute or relative to the application root.
    /// </summary>
    public string Directory { get; set; } = "_cache/images";

    /// <summary>
    /// Number of days to retain images in the cache, counted from the file's last update.
    /// Images older than this will be automatically cleaned up.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// How often the cleanup looks for expired images, in minutes. A sweep only removes files older than
    /// <see cref="RetentionDays"/> — it never empties the cache.
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Whether to convert images to WebP format for storage optimization.
    /// </summary>
    public bool ConvertToWebP { get; set; } = false;

    /// <summary>
    /// WebP quality when converting (1-100).
    /// Only used when ConvertToWebP is true.
    /// </summary>
    public int WebPQuality { get; set; } = 85;
}
