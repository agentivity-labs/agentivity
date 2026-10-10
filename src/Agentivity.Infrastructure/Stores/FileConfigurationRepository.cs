using System.Text.Json;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// File-based storage for JSON configuration files.
/// Handles persistence, caching, and file I/O operations.
/// </summary>
public sealed class FileConfigurationRepository : IConfigurationRepository
{
    private readonly string _configPath;
    private readonly ILogger<FileConfigurationRepository> _logger;
    private readonly Dictionary<string, (object Data, DateTime LoadedAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Configuration;

    public FileConfigurationRepository(IOptions<ConfigurationOptions> options, ILogger<FileConfigurationRepository> logger)
    {
        _logger = logger;
        _configPath = PathResolver.Resolve(options.Value.StoragePath!);
        _logger.LogInformation("Configuration store using path: {Path}", _configPath);
    }

    /// <summary>
    /// Gets the base configuration path.
    /// </summary>
    public string BasePath => _configPath;

    /// <summary>
    /// Loads a JSON file and deserializes it to the specified type.
    /// Uses caching with file modification time tracking.
    /// </summary>
    public async Task<T?> LoadAsync<T>(string relativePath, CancellationToken cancellationToken = default)
        where T : class
    {
        var fullPath = Path.Combine(_configPath, relativePath);

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Configuration file not found: {Path}", fullPath);
            return null;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(fullPath);

            if (_cache.TryGetValue(fullPath, out var cached) && cached.LoadedAt >= lastWrite)
            {
                return cached.Data as T;
            }

            var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var data = JsonSerializer.Deserialize<T>(json, JsonOptions);

            if (data is not null)
            {
                _cache[fullPath] = (data, lastWrite);
                _logger.LogDebug("Loaded configuration: {Path}", relativePath);
            }

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configuration: {Path}", fullPath);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Loads a JSON file synchronously and deserializes it to the specified type.
    /// </summary>
    public T? Load<T>(string relativePath)
        where T : class
    {
        var fullPath = Path.Combine(_configPath, relativePath);

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Configuration file not found: {Path}", fullPath);
            return null;
        }

        _lock.Wait();
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(fullPath);

            if (_cache.TryGetValue(fullPath, out var cached) && cached.LoadedAt >= lastWrite)
            {
                return cached.Data as T;
            }

            var json = File.ReadAllText(fullPath);
            var data = JsonSerializer.Deserialize<T>(json, JsonOptions);

            if (data is not null)
            {
                _cache[fullPath] = (data, lastWrite);
                _logger.LogDebug("Loaded configuration: {Path}", relativePath);
            }

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configuration: {Path}", fullPath);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves data to a JSON file.
    /// </summary>
    public async Task SaveAsync<T>(string relativePath, T data, CancellationToken cancellationToken = default)
        where T : class
    {
        ValidatePath(relativePath);

        var fullPath = Path.Combine(_configPath, relativePath);
        EnsureDirectoryExists(fullPath);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            await File.WriteAllTextAsync(fullPath, json, cancellationToken);

            var lastWrite = File.GetLastWriteTimeUtc(fullPath);
            _cache[fullPath] = (data, lastWrite);

            _logger.LogInformation("Saved configuration: {Path}", relativePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration: {Path}", fullPath);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves raw JSON content to a file.
    /// </summary>
    public async Task SaveRawAsync(string relativePath, string jsonContent, CancellationToken cancellationToken = default)
    {
        ValidatePath(relativePath);

        var fullPath = Path.Combine(_configPath, relativePath);
        EnsureDirectoryExists(fullPath);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            JsonDocument.Parse(jsonContent); // Validate JSON

            await File.WriteAllTextAsync(fullPath, jsonContent, cancellationToken);
            _cache.Remove(fullPath);

            _logger.LogInformation("Saved configuration (raw): {Path}", relativePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save configuration: {Path}", fullPath);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Lists all JSON files in the configuration directory.
    /// </summary>
    public IEnumerable<ConfigurationFileInfo> ListFiles(string? subDirectory = null)
    {
        var searchPath = subDirectory is not null ? Path.Combine(_configPath, subDirectory) : _configPath;

        if (!Directory.Exists(searchPath))
        {
            return Enumerable.Empty<ConfigurationFileInfo>();
        }

        return Directory
            .GetFiles(searchPath, "*.json", SearchOption.AllDirectories)
            .Select(fullPath =>
            {
                var relativePath = Path.GetRelativePath(_configPath, fullPath).Replace("\\", "/");
                var fileInfo = new FileInfo(fullPath);
                return new ConfigurationFileInfo(relativePath, Path.GetFileNameWithoutExtension(fullPath), fileInfo.Length, fileInfo.LastWriteTimeUtc);
            })
            .OrderBy(f => f.Path);
    }

    /// <summary>
    /// Checks if a configuration file exists.
    /// </summary>
    public bool Exists(string relativePath)
    {
        if (relativePath.Contains(".."))
        {
            return false;
        }

        var fullPath = Path.Combine(_configPath, relativePath);
        return File.Exists(fullPath);
    }

    /// <summary>
    /// Invalidates the cache for a specific file or all files.
    /// </summary>
    public void InvalidateCache(string? relativePath = null)
    {
        _lock.Wait();
        try
        {
            if (relativePath is null)
            {
                _cache.Clear();
                _logger.LogInformation("Configuration cache cleared");
            }
            else
            {
                var fullPath = Path.Combine(_configPath, relativePath);
                _cache.Remove(fullPath);
                _logger.LogDebug("Configuration cache invalidated: {Path}", relativePath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void ValidatePath(string relativePath)
    {
        if (relativePath.Contains(".."))
        {
            throw new ArgumentException("Invalid path: path traversal not allowed", nameof(relativePath));
        }
    }

    private void EnsureDirectoryExists(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogInformation("Created configuration subdirectory: {Directory}", directory);
        }
    }
}

/// <summary>
/// Represents information about a configuration file.
/// </summary>
public sealed record ConfigurationFileInfo(string Path, string Name, long Size, DateTime LastModified);
