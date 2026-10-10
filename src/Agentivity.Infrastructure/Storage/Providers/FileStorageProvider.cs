using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Storage.Providers;

/// <summary>
/// File-based storage provider using a single JSON file with thread-safe operations.
/// Stores all key-value pairs in one JSON file for simplicity and atomicity.
/// </summary>
public sealed class FileStorageProvider : IStorageProvider, IDisposable
{
    private readonly string _filePath;
    private readonly ILogger<FileStorageProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public string ProviderName => "File";

    public FileStorageProvider(string filePath, ILogger<FileStorageProvider> logger)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Ensure directory exists
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Initialize file if it doesn't exist
        if (!File.Exists(_filePath))
        {
            File.WriteAllText(_filePath, "{}");
        }
    }

    public async ValueTask<StorageResult<string>> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadDataAsync(cancellationToken);

            if (!data.TryGetValue(key, out var entry))
            {
                return StorageResult<string>.Fail($"Key '{key}' not found");
            }

            // Check expiration
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                // Remove expired entry
                data.Remove(key);
                await SaveDataAsync(data, cancellationToken);
                return StorageResult<string>.Fail($"Key '{key}' has expired");
            }

            return StorageResult<string>.Ok(entry.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read key '{Key}' from file storage", key);
            return StorageResult<string>.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult> WriteAsync(string key, string value, StorageWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadDataAsync(cancellationToken);

            // Check if key exists and overwrite is disabled
            if (data.ContainsKey(key) && options?.Overwrite == false)
            {
                return StorageResult.Fail($"Key '{key}' already exists and overwrite is disabled");
            }

            var entry = new StorageEntry
            {
                Value = value,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = options?.Expiration.HasValue == true ? DateTimeOffset.UtcNow.Add(options.Expiration.Value) : null,
                Metadata = options?.Metadata ?? new Dictionary<string, string>(),
            };

            data[key] = entry;
            await SaveDataAsync(data, cancellationToken);

            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write key '{Key}' to file storage", key);
            return StorageResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<StorageResult> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadDataAsync(cancellationToken);

            if (!data.Remove(key))
            {
                return StorageResult.Fail($"Key '{key}' not found");
            }

            await SaveDataAsync(data, cancellationToken);
            return StorageResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete key '{Key}' from file storage", key);
            return StorageResult.Fail(ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadDataAsync(cancellationToken);

            if (!data.TryGetValue(key, out var entry))
            {
                return false;
            }

            // Check expiration
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < DateTimeOffset.UtcNow)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check existence of key '{Key}' in file storage", key);
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<IReadOnlyList<string>> ListKeysAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var data = await LoadDataAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;

            // Filter out expired entries
            var validKeys = data.Where(kvp => !kvp.Value.ExpiresAt.HasValue || kvp.Value.ExpiresAt.Value >= now).Select(kvp => kvp.Key);

            // Apply pattern filter if provided
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
                validKeys = validKeys.Where(k => regex.IsMatch(k));
            }

            return validKeys.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list keys from file storage");
            return Array.Empty<string>();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, StorageEntry>> LoadDataAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, StorageEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);

        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new Dictionary<string, StorageEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var data = JsonSerializer.Deserialize<Dictionary<string, StorageEntry>>(json, _jsonOptions);
        return data ?? new Dictionary<string, StorageEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveDataAsync(Dictionary<string, StorageEntry> data, CancellationToken cancellationToken)
    {
        // Write to temp file first, then rename (atomic operation)
        var tempFile = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        await File.WriteAllTextAsync(tempFile, json, cancellationToken);

        // Atomic replace
        File.Move(tempFile, _filePath, true);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key cannot be null or whitespace", nameof(key));
        }

        if (key.Length > 255)
        {
            throw new ArgumentException("Key cannot exceed 255 characters", nameof(key));
        }

        // Prevent path traversal
        if (key.Contains("..") || key.Contains("\\") || key.Contains("/"))
        {
            throw new ArgumentException("Key cannot contain path separators or '..'", nameof(key));
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
    }

    private sealed class StorageEntry
    {
        public string Value { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }
}
