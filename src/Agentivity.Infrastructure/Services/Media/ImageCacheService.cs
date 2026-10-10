using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Services.Media;

/// <summary>
/// Service for caching images from various sources (URL, base64, file upload).
/// Manages the image cache directory and handles cleanup of expired images.
/// </summary>
public sealed class ImageCacheService
{
    private readonly string _cacheDirectory;
    private readonly ImageCacheOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IMediaResourceUrlResolver _urlResolver;
    private readonly ILogger<ImageCacheService> _logger;

    public ImageCacheService(IOptions<ImageCacheOptions> options, HttpClient httpClient, IMediaResourceUrlResolver urlResolver, ILogger<ImageCacheService> logger)
    {
        _options = options?.Value ?? new ImageCacheOptions();
        _cacheDirectory = PathResolver.Resolve(_options.Directory);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _urlResolver = urlResolver ?? throw new ArgumentNullException(nameof(urlResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Gets the cache directory path.
    /// </summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// Stores an image from a URL into the cache.
    /// </summary>
    public async ValueTask<ImageRef> CacheFromUrlAsync(string url, string? desiredSize = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Caching image from URL: {Url}", url);

        var id = Guid.NewGuid().ToString("N");
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        var extension = GetExtensionFromMimeType(contentType);
        var fileName = $"{id}{extension}";
        var localPath = Path.Combine(_cacheDirectory, fileName);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = File.Create(localPath);
        await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

        var fileInfo = new FileInfo(localPath);

        _logger.LogDebug("Image cached from URL: {FileName} ({Size} bytes)", fileName, fileInfo.Length);

        return new ImageRef
        {
            Id = id,
            Url = url,
            LocalPath = localPath,
            FileName = fileName,
            MimeType = contentType,
            FileSizeBytes = fileInfo.Length,
            DesiredSize = desiredSize,
            CachedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Stores an image from base64 data into the cache.
    /// </summary>
    public async ValueTask<ImageRef> CacheFromBase64Async(string base64Data, string mimeType = "image/png", string? desiredSize = null, CancellationToken cancellationToken = default)
    {
        // Check cancellation before starting
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("Caching image from base64 data");

        var id = Guid.NewGuid().ToString("N");
        var extension = GetExtensionFromMimeType(mimeType);
        var fileName = $"{id}{extension}";
        var localPath = Path.Combine(_cacheDirectory, fileName);

        var bytes = Convert.FromBase64String(base64Data);

        // Write file without cancellation token - file write is fast and should complete
        await File.WriteAllBytesAsync(localPath, bytes).ConfigureAwait(false);

        _logger.LogDebug("Image cached from base64: {FileName} ({Size} bytes)", fileName, bytes.Length);

        var imageRef = new ImageRef
        {
            Id = id,
            Url = _urlResolver.ResolveUrl(id),
            LocalPath = localPath,
            FileName = fileName,
            MimeType = mimeType,
            FileSizeBytes = bytes.Length,
            DesiredSize = desiredSize,
            CachedAt = DateTimeOffset.UtcNow,
        };

        return imageRef;
    }

    /// <summary>
    /// Stores a local file into the cache (copy).
    /// </summary>
    public async ValueTask<ImageRef> CacheFromFileAsync(string sourcePath, string? desiredSize = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Caching image from file: {SourcePath}", sourcePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source image file not found.", sourcePath);
        }

        var id = Guid.NewGuid().ToString("N");
        var extension = Path.GetExtension(sourcePath);
        var fileName = $"{id}{extension}";
        var localPath = Path.Combine(_cacheDirectory, fileName);

        await using var source = File.OpenRead(sourcePath);
        await using var dest = File.Create(localPath);
        await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);

        var fileInfo = new FileInfo(localPath);

        _logger.LogDebug("Image cached from file: {FileName} ({Size} bytes)", fileName, fileInfo.Length);

        return new ImageRef
        {
            Id = id,
            Url = _urlResolver.ResolveUrl(id),
            LocalPath = localPath,
            FileName = fileName,
            MimeType = GuessMimeType(extension),
            FileSizeBytes = fileInfo.Length,
            DesiredSize = desiredSize,
            CachedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Stores raw bytes into the cache.
    /// </summary>
    public async ValueTask<ImageRef> CacheFromBytesAsync(byte[] bytes, string mimeType = "image/png", string? desiredSize = null, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var extension = GetExtensionFromMimeType(mimeType);
        var fileName = $"{id}{extension}";
        var localPath = Path.Combine(_cacheDirectory, fileName);

        await File.WriteAllBytesAsync(localPath, bytes, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Image cached from bytes: {FileName} ({Size} bytes)", fileName, bytes.Length);

        return new ImageRef
        {
            Id = id,
            Url = _urlResolver.ResolveUrl(id),
            LocalPath = localPath,
            FileName = fileName,
            MimeType = mimeType,
            FileSizeBytes = bytes.Length,
            DesiredSize = desiredSize,
            CachedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Gets the full path for a cached image by ID.
    /// </summary>
    public string? GetCachedImagePath(string imageId)
    {
        var files = Directory.GetFiles(_cacheDirectory, $"{imageId}.*");
        return files.Length > 0 ? files[0] : null;
    }

    /// <summary>
    /// Checks if an image exists in the cache.
    /// </summary>
    public bool Exists(string imageId)
    {
        return GetCachedImagePath(imageId) != null;
    }

    /// <summary>
    /// Deletes an image from the cache.
    /// </summary>
    public bool Delete(string imageId)
    {
        var path = GetCachedImagePath(imageId);
        if (path == null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            _logger.LogDebug("Deleted cached image: {ImageId}", imageId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached image: {ImageId}", imageId);
            return false;
        }
    }

    /// <summary>
    /// Cleans up expired images from the cache.
    /// </summary>
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
                    _logger.LogDebug("Deleted expired cache file: {FileName}", fileInfo.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete cache file: {FileName}", fileInfo.Name);
                }
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired images from cache", deletedCount);
        }

        return ValueTask.FromResult(deletedCount);
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public (int FileCount, long TotalSizeBytes) GetCacheStats()
    {
        var files = Directory.GetFiles(_cacheDirectory);
        var totalSize = 0L;

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            totalSize += fileInfo.Length;
        }

        return (files.Length, totalSize);
    }

    /// <summary>
    /// Gets or creates a placeholder image for the specified size.
    /// Placeholder images are reused across requests with the same size.
    /// </summary>
    public ImageRef GetOrCreatePlaceholder(string size)
    {
        var normalizedSize = NormalizePlaceholderSize(size);
        var placeholderId = $"placeholder_{normalizedSize}";
        var fileName = $"{placeholderId}.png";
        var placeholderPath = Path.Combine(_cacheDirectory, fileName);

        // Check if placeholder already exists
        if (File.Exists(placeholderPath))
        {
            var existingFileInfo = new FileInfo(placeholderPath);
            return new ImageRef
            {
                Id = placeholderId,
                Url = _urlResolver.ResolveUrl(placeholderId),
                LocalPath = placeholderPath,
                FileName = fileName,
                MimeType = "image/png",
                FileSizeBytes = existingFileInfo.Length,
                DesiredSize = size,
                CachedAt = DateTimeOffset.UtcNow,
            };
        }

        // Generate placeholder image
        var (width, height) = ParseSizeDimensions(normalizedSize);
        var data = GeneratePlaceholderPng(width, height);

        File.WriteAllBytes(placeholderPath, data);

        _logger.LogDebug("Created placeholder image: {FileName} ({Width}x{Height})", fileName, width, height);

        return new ImageRef
        {
            Id = placeholderId,
            Url = _urlResolver.ResolveUrl(placeholderId),
            LocalPath = placeholderPath,
            FileName = fileName,
            MimeType = "image/png",
            FileSizeBytes = data.Length,
            DesiredSize = size,
            CachedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string NormalizePlaceholderSize(string size)
    {
        // Map friendly size names to dimensions
        return size?.Trim().ToLowerInvariant() switch
        {
            "landscape_large" or "landscape" or "wide" or "2048x1024" => "2048x1024",
            "landscape_medium" or "1536x1024" => "1536x1024",
            "portrait_large" or "portrait" or "1024x2048" => "1024x2048",
            "square_large" or "square" or "square_medium" or "1024x1024" => "1024x1024",
            "square_small" or "512x512" => "512x512",
            "thumbnail" or "256x256" => "256x256",
            _ => "1024x1024",
        };
    }

    private static (int Width, int Height) ParseSizeDimensions(string normalizedSize)
    {
        var parts = normalizedSize.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
        {
            return (width, height);
        }

        return (1024, 1024);
    }

    private static byte[] GeneratePlaceholderPng(int width, int height)
    {
        // Generate a simple PNG with a solid gray background and centered text
        // This creates a minimal valid PNG file
        using var ms = new MemoryStream();

        // PNG signature
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR chunk
        WriteChunk(
            ms,
            "IHDR",
            writer =>
            {
                writer.Write(ToBigEndian(width));
                writer.Write(ToBigEndian(height));
                writer.Write((byte)8); // bit depth
                writer.Write((byte)2); // color type (RGB)
                writer.Write((byte)0); // compression
                writer.Write((byte)0); // filter
                writer.Write((byte)0); // interlace
            }
        );

        // IDAT chunk - compressed image data
        var rawData = new byte[(width * 3 + 1) * height];
        var idx = 0;
        for (var y = 0; y < height; y++)
        {
            rawData[idx++] = 0; // filter byte (none)
            for (var x = 0; x < width; x++)
            {
                // Gray color (#808080)
                rawData[idx++] = 0x80;
                rawData[idx++] = 0x80;
                rawData[idx++] = 0x80;
            }
        }

        using var compressedMs = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(compressedMs, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(rawData);
        }

        var compressedData = compressedMs.ToArray();

        // Wrap in zlib format (header + data + adler32)
        var zlibData = new byte[2 + compressedData.Length + 4];
        zlibData[0] = 0x78; // zlib header
        zlibData[1] = 0x01; // fastest compression
        Array.Copy(compressedData, 0, zlibData, 2, compressedData.Length);
        var adler = ComputeAdler32(rawData);
        zlibData[^4] = (byte)(adler >> 24);
        zlibData[^3] = (byte)(adler >> 16);
        zlibData[^2] = (byte)(adler >> 8);
        zlibData[^1] = (byte)adler;

        WriteChunk(ms, "IDAT", writer => writer.Write(zlibData));

        // IEND chunk
        WriteChunk(ms, "IEND", _ => { });

        return ms.ToArray();
    }

    private static void WriteChunk(MemoryStream ms, string type, Action<BinaryWriter> writeData)
    {
        using var dataMs = new MemoryStream();
        using (var writer = new BinaryWriter(dataMs, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writeData(writer);
        }

        var data = dataMs.ToArray();
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);

        // Length
        ms.Write(ToBigEndianBytes(data.Length));

        // Type + Data for CRC
        var crcData = new byte[4 + data.Length];
        Array.Copy(typeBytes, 0, crcData, 0, 4);
        Array.Copy(data, 0, crcData, 4, data.Length);

        // Type
        ms.Write(typeBytes);

        // Data
        ms.Write(data);

        // CRC
        var crc = ComputeCrc32(crcData);
        ms.Write(ToBigEndianBytes((int)crc));
    }

    private static byte[] ToBigEndianBytes(int value)
    {
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static int ToBigEndian(int value)
    {
        return ((value & 0xFF) << 24) | ((value & 0xFF00) << 8) | ((value >> 8) & 0xFF00) | ((value >> 24) & 0xFF);
    }

    private static uint ComputeAdler32(byte[] data)
    {
        uint a = 1,
            b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 * (crc & 1));
            }
        }
        return ~crc;
    }

    private static string GetExtensionFromMimeType(string mimeType)
    {
        return mimeType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            _ => ".bin",
        };
    }

    private static string GuessMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
    }
}
