namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// Store interface for persisting and retrieving configuration files.
/// </summary>
public interface IConfigurationRepository
{
    /// <summary>
    /// Loads a JSON file and deserializes it to the specified type.
    /// </summary>
    Task<T?> LoadAsync<T>(string relativePath, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Loads a JSON file synchronously and deserializes it to the specified type.
    /// </summary>
    T? Load<T>(string relativePath)
        where T : class;

    /// <summary>
    /// Saves data to a JSON file.
    /// </summary>
    Task SaveAsync<T>(string relativePath, T data, CancellationToken cancellationToken = default)
        where T : class;

    /// <summary>
    /// Saves raw JSON content to a file.
    /// </summary>
    Task SaveRawAsync(string relativePath, string jsonContent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all JSON files in the configuration directory.
    /// </summary>
    IEnumerable<ConfigurationFileInfo> ListFiles(string? subDirectory = null);

    /// <summary>
    /// Checks if a configuration file exists.
    /// </summary>
    bool Exists(string relativePath);
}
