namespace Agentivity.Infrastructure.Storage;

/// <summary>
/// Generic storage provider interface - abstracts file, database, cloud storage, etc.
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Storage provider name (e.g., "File", "Supabase", "Azure Blob")
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Reads a value from storage by key
    /// </summary>
    ValueTask<StorageResult<string>> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a value to storage
    /// </summary>
    ValueTask<StorageResult> WriteAsync(string key, string value, StorageWriteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a value from storage
    /// </summary>
    ValueTask<StorageResult> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a key exists in storage
    /// </summary>
    ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all keys in storage matching an optional pattern
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListKeysAsync(string? pattern = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Storage operation result
/// </summary>
public sealed class StorageResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public static StorageResult Ok() => new() { Success = true };

    public static StorageResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Storage operation result with value
/// </summary>
public sealed class StorageResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }

    public static StorageResult<T> Ok(T value) => new() { Success = true, Value = value };

    public static StorageResult<T> Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Options for write operations
/// </summary>
public sealed class StorageWriteOptions
{
    public bool Overwrite { get; init; } = true;
    public TimeSpan? Expiration { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
