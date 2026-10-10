using System.Text.Json;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Models;
using Agentivity.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// File-based store for application settings.
/// Reads storage path from AppSettings:StoragePath in configuration.
/// </summary>
public sealed class FileAppSettingsRepository : IAppSettingsRepository
{
    private readonly string _filePath;
    private readonly ILogger<FileAppSettingsRepository> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public FileAppSettingsRepository(IOptions<AppSettingsOptions> options, ILogger<FileAppSettingsRepository> logger)
    {
        _logger = logger;
        _filePath = PathResolver.Resolve(options.Value.StoragePath!);
        _logger.LogInformation("AppSettings store using path: {FilePath}", _filePath);
    }

    public bool Exists()
    {
        return File.Exists(_filePath);
    }

    public async Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("Settings file not found at {FilePath}", _filePath);
            return null;
        }

        var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

        _logger.LogInformation("Loaded application settings from {FilePath}", _filePath);
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings is null)
            throw new ArgumentNullException(nameof(settings));

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);

        _logger.LogInformation("Saved application settings to {FilePath}", _filePath);
    }
}
