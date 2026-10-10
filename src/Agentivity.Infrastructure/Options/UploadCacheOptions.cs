namespace Agentivity.Infrastructure.Options;

/// <summary>
/// Configuration options for the upload cache — files a person hands to a run (a CV, a contract…),
/// the input-side counterpart of <see cref="ImageCacheOptions"/> / <see cref="AudioCacheOptions"/>.
/// </summary>
public sealed class UploadCacheOptions
{
    public const string SectionName = "UploadCache";

    /// <summary>Directory for uploaded files. Relative paths resolve from the solution root, like every other cache.</summary>
    public string Directory { get; set; } = "_cache/uploads";

    /// <summary>Uploads not updated for this many days are cleaned up automatically.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// How often the cleanup looks for expired uploads, in minutes. A sweep only removes files older than
    /// <see cref="RetentionDays"/> — it never empties the cache.
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 60;

    /// <summary>Largest accepted upload, in megabytes.</summary>
    public int MaxFileSizeMb { get; set; } = 10;
}
