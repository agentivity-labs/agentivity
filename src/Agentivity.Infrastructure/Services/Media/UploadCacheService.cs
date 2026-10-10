using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Services.Media;

/// <summary>What is known about an uploaded file — the reference a run passes around instead of the bytes.</summary>
public sealed record UploadRef(string Id, string Name, string MimeType, long SizeBytes, DateTimeOffset CachedAt);

/// <summary>
/// Cache for files a person uploads to a run — same mechanism as <see cref="ImageCacheService"/> and
/// <see cref="AudioCacheService"/>: each file is stored under a GUID-derived name in the one <c>_cache</c>
/// directory, and <see cref="UploadCacheCleanupService"/> removes it after the retention period. Unlike
/// generated media, an upload is private input: it is never served over HTTP, only read back
/// server-side by id (a workflow node, an agent tool).
/// </summary>
public sealed class UploadCacheService
{
    private static readonly Regex IdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private const string MetaSuffix = ".meta.json";

    private readonly string _cacheDirectory;
    private readonly UploadCacheOptions _options;
    private readonly ILogger<UploadCacheService> _logger;

    public UploadCacheService(IOptions<UploadCacheOptions> options, ILogger<UploadCacheService> logger)
    {
        _options = options?.Value ?? new UploadCacheOptions();
        _cacheDirectory = PathResolver.Resolve(_options.Directory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Directory.CreateDirectory(_cacheDirectory);
    }

    public string CacheDirectory => _cacheDirectory;

    public long MaxFileSizeBytes => (long)_options.MaxFileSizeMb * 1024 * 1024;

    /// <summary>Stores an upload and returns the reference to hand to the run.</summary>
    public async ValueTask<UploadRef> SaveAsync(Stream content, string? originalName, string? mimeType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var id = Guid.NewGuid().ToString("N");
        var name = SanitizeName(originalName);
        var path = Path.Combine(_cacheDirectory, $"{id}{ExtensionOf(name)}");

        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var uploaded = new UploadRef(id, name, string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType, new FileInfo(path).Length, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(_cacheDirectory, $"{id}{MetaSuffix}"), JsonSerializer.Serialize(uploaded), cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Upload cached: {Id} '{Name}' ({Size} bytes)", id, name, uploaded.SizeBytes);
        return uploaded;
    }

    /// <summary>The upload's metadata, or null when the id is unknown, malformed or already cleaned up.</summary>
    public UploadRef? Find(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var metaPath = Path.Combine(_cacheDirectory, $"{id}{MetaSuffix}");
        if (!File.Exists(metaPath) || GetPath(id) is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UploadRef>(File.ReadAllText(metaPath));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The upload's bytes, or null when it is unknown or already cleaned up.</summary>
    public async ValueTask<byte[]?> ReadBytesAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = IsValidId(id) ? GetPath(id) : null;
        return path is null ? null : await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id))
        {
            return false;
        }

        var deleted = false;
        foreach (var path in Directory.GetFiles(_cacheDirectory, $"{id}.*"))
        {
            try
            {
                File.Delete(path);
                deleted = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete cached upload file: {Path}", path);
            }
        }

        return deleted;
    }

    /// <summary>Removes uploads not updated for longer than the retention period (a file and its metadata go together); newer files are left alone.</summary>
    public ValueTask<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
        var deletedCount = 0;

        foreach (var file in Directory.GetFiles(_cacheDirectory))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                if (!info.Name.EndsWith(MetaSuffix, StringComparison.Ordinal))
                {
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete expired upload file: {FileName}", info.Name);
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired uploads from cache", deletedCount);
        }

        return ValueTask.FromResult(deletedCount);
    }

    public (int FileCount, long TotalSizeBytes) GetCacheStats()
    {
        var files = Directory.GetFiles(_cacheDirectory).Where(f => !f.EndsWith(MetaSuffix, StringComparison.Ordinal)).ToArray();
        return (files.Length, files.Sum(f => new FileInfo(f).Length));
    }

    private static bool IsValidId(string? id) => IdPattern.IsMatch(id ?? string.Empty);

    private string? GetPath(string id) => Directory.GetFiles(_cacheDirectory, $"{id}.*").FirstOrDefault(f => !f.EndsWith(MetaSuffix, StringComparison.Ordinal));

    /// <summary>The name a person sees back: the file name only, never a path.</summary>
    private static string SanitizeName(string? originalName)
    {
        var name = Path.GetFileName((originalName ?? string.Empty).Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? "upload" : name;
    }

    /// <summary>Kept on the stored file because extractors pick a reader from it; restricted so a crafted name cannot escape the directory.</summary>
    private static string ExtensionOf(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension.Length is > 1 and <= 9 && extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? extension : string.Empty;
    }
}
