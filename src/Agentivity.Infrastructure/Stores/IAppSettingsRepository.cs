using Agentivity.Infrastructure.Models;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// Store for application-level settings.
/// </summary>
public interface IAppSettingsRepository
{
    /// <summary>
    /// Loads the application settings.
    /// </summary>
    Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the application settings.
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the settings file exists.
    /// </summary>
    bool Exists();
}
