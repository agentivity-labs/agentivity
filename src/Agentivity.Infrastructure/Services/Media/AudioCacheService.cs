using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Services.Media;

/// <summary>
/// Service for caching audio files (currently: text-to-speech output). The audio counterpart of
/// <see cref="ImageCacheService"/>, trimmed to what audio actually needs — a single "cache these
/// raw bytes" entry point, no URL/base64/file-upload variants and no placeholder generation
/// (there is no meaningful "placeholder sound" the way there's a placeholder image).
/// </summary>
public sealed class AudioCacheService
{
    private readonly string _cacheDirectory;
    private readonly AudioCacheOptions _options;
    private readonly IMediaResourceUrlResolver _urlResolver;
    private readonly ILogger<AudioCacheService> _logger;

    public AudioCacheService(IOptions<AudioCacheOptions> options, IMediaResourceUrlResolver urlResolver, ILogger<AudioCacheService> logger)
    {
        _options = options?.Value ?? new AudioCacheOptions();
        _cacheDirectory = PathResolver.Resolve(_options.Directory);
        _urlResolver = urlResolver ?? throw new ArgumentNullException(nameof(urlResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Directory.CreateDirectory(_cacheDirectory);
    }

    public string CacheDirectory => _cacheDirectory;

    /// <summary>Stores raw audio bytes into the cache.</summary>
    public async ValueTask<AudioRef> CacheFromBytesAsync(byte[] bytes, string mimeType = "audio/mpeg", double? durationSeconds = null, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var extension = GetExtensionFromMimeType(mimeType);
        var fileName = $"{id}{extension}";
        var localPath = Path.Combine(_cacheDirectory, fileName);

        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Audio cached from bytes: {FileName} ({Size} bytes)", fileName, bytes.Length);

        return new AudioRef
        {
            Id = id,
            Url = _urlResolver.ResolveUrl(id),
            LocalPath = localPath,
            FileName = fileName,
            MimeType = mimeType,
            DurationSeconds = durationSeconds,
            FileSizeBytes = bytes.Length,
            CachedAt = DateTimeOffset.UtcNow,
        };
    }

    public string? GetCachedAudioPath(string audioId)
    {
        var files = Directory.GetFiles(_cacheDirectory, $"{audioId}.*");
        return files.Length > 0 ? files[0] : null;
    }

    public bool Delete(string audioId)
    {
        var path = GetCachedAudioPath(audioId);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            _logger.LogDebug("Deleted cached audio: {AudioId}", audioId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached audio: {AudioId}", audioId);
            return false;
        }
    }

    public ValueTask<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);
        var files = Directory.GetFiles(_cacheDirectory);
        var deletedCount = 0;

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var fileInfo = new FileInfo(file);
            if (fileInfo.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                try
                {
                    File.Delete(file);
                    deletedCount++;
                    _logger.LogDebug("Deleted expired audio cache file: {FileName}", fileInfo.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete audio cache file: {FileName}", fileInfo.Name);
                }
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired audio files from cache", deletedCount);
        }

        return ValueTask.FromResult(deletedCount);
    }

    public (int FileCount, long TotalSizeBytes) GetCacheStats()
    {
        var files = Directory.GetFiles(_cacheDirectory);
        long totalSize = 0;

        foreach (var file in files)
        {
            totalSize += new FileInfo(file).Length;
        }

        return (files.Length, totalSize);
    }

    private static string GetExtensionFromMimeType(string mimeType) =>
        mimeType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/ogg" => ".ogg",
            "audio/webm" => ".webm",
            "audio/flac" => ".flac",
            "audio/aac" => ".aac",
            _ => ".bin",
        };
}
