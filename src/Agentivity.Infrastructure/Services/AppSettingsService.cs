using Agentivity.Infrastructure.Models;
using Agentivity.Infrastructure.Stores;

namespace Agentivity.Infrastructure.Workflow.Services;

/// <summary>
/// Service for managing application-level settings.
/// Settings are stored in _data/settings.json and editable via UI.
/// </summary>
public sealed class AppSettingsService
{
    private readonly IAppSettingsRepository _store;
    private AppSettings? _cachedSettings;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppSettingsService(IAppSettingsRepository store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Gets the current application settings.
    /// Returns default settings if file doesn't exist (without saving).
    /// </summary>
    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedSettings is not null)
            return _cachedSettings;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSettings is not null)
                return _cachedSettings;

            // Load settings if exists, otherwise return defaults (don't save automatically)
            if (_store.Exists())
            {
                _cachedSettings = await _store.LoadAsync(cancellationToken);
            }

            // Return default settings if file doesn't exist
            // Settings will only be persisted when explicitly saved via API
            _cachedSettings ??= new AppSettings();

            return _cachedSettings;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Updates the application settings.
    /// </summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await _store.SaveAsync(settings, cancellationToken);
            _cachedSettings = settings;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Invalidates the cached settings, forcing a reload on next access.
    /// </summary>
    public void InvalidateCache()
    {
        _cachedSettings = null;
    }
}
