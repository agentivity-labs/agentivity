namespace Agentivity.Infrastructure.Options;

/// <summary>
/// Configuration options for the audio cache service — the audio counterpart of
/// <see cref="ImageCacheOptions"/>.
/// </summary>
public sealed class AudioCacheOptions
{
    public const string SectionName = "AudioCache";

    /// <summary>
    /// Directory path for storing cached audio files. Can be absolute or relative to the
    /// application root.
    /// </summary>
    public string Directory { get; set; } = "_cache/audio";

    /// <summary>
    /// Number of days to retain audio files in the cache, counted from the file's last update. Files
    /// older than this are automatically cleaned up.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// How often the cleanup looks for expired audio files, in minutes. A sweep only removes files older than
    /// <see cref="RetentionDays"/> — it never empties the cache.
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 60;
}
