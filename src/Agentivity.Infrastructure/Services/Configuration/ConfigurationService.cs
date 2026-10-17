using Agentivity.Infrastructure.Stores;

namespace Agentivity.Infrastructure.Workflow.Services.Configuration;

/// <summary>
/// Service for managing configuration files.
/// Provides business logic layer over the configuration store.
/// </summary>
public sealed class ConfigurationService
{
    private readonly IConfigurationRepository _store;

    public ConfigurationService(IConfigurationRepository store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Loads a JSON configuration file and deserializes it to the specified type.
    /// </summary>
    public Task<T?> LoadAsync<T>(string relativePath, CancellationToken cancellationToken = default)
        where T : class => _store.LoadAsync<T>(relativePath, cancellationToken);

    /// <summary>
    /// Loads a JSON configuration file synchronously and deserializes it to the specified type.
    /// </summary>
    public T? Load<T>(string relativePath)
        where T : class => _store.Load<T>(relativePath);

    /// <summary>
    /// Saves data to a JSON configuration file.
    /// Automatically notifies registered components to reload.
    /// </summary>
    public async Task SaveAsync<T>(string relativePath, T data, CancellationToken cancellationToken = default)
        where T : class
    {
        await _store.SaveAsync(relativePath, data, cancellationToken);

        // Notify registered components to reload
        ConfigurationRegistry.NotifyConfigurationChanged(relativePath);
    }

    /// <summary>
    /// Saves raw JSON content to a configuration file.
    /// Automatically notifies registered components to reload.
    /// </summary>
    public async Task SaveRawAsync(string relativePath, string jsonContent, CancellationToken cancellationToken = default)
    {
        await _store.SaveRawAsync(relativePath, jsonContent, cancellationToken);

        // Notify registered components to reload
        ConfigurationRegistry.NotifyConfigurationChanged(relativePath);
    }

    /// <summary>
    /// Lists all configuration files in the directory.
    /// </summary>
    public IEnumerable<ConfigurationFileInfo> ListFiles(string? subDirectory = null) => _store.ListFiles(subDirectory);

    /// <summary>
    /// Checks if a configuration file exists.
    /// </summary>
    public bool Exists(string relativePath) => _store.Exists(relativePath);
}
