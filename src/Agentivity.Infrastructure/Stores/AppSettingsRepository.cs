using System.Text.Json;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Models;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// The settings of the installation (which connection each assistant uses, what is turned on), a domain of the workspace: one
/// object, kept by the workspace module the edition loads, beside the connections it names. The settings file of before
/// (<c>AppSettings:StoragePath</c>) is taken over the first time they are read, and left as it was. The object carries a name, as
/// every workspace object: some workspace modules keep only what has one.
/// </summary>
public sealed class AppSettingsRepository : IAppSettingsRepository
{
    public const string SettingsType = "settings";
    public const string SettingsId = "installation";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly IWorkspaceObjectStore _store;
    private readonly string _legacyFile;
    private readonly ILogger<AppSettingsRepository> _logger;

    public AppSettingsRepository(IWorkspaceObjectStore store, AppSettingsOptions options, ILogger<AppSettingsRepository> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _legacyFile = PathResolver.Resolve(string.IsNullOrWhiteSpace(options.StoragePath) ? "_data/settings.json" : options.StoragePath);
        _logger = logger;
    }

    /// <summary>Whether settings were ever saved — in the workspace, or in the file of before still to take over.</summary>
    public bool Exists() => _store.ExistsAsync(SettingsType, SettingsId).AsTask().GetAwaiter().GetResult() || File.Exists(_legacyFile);

    public async Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var kept = await _store.GetAsync(SettingsType, SettingsId, cancellationToken).ConfigureAwait(false);
        if (kept.Success)
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(kept.Value!.Payload);
            return (root?["settings"] ?? root).Deserialize<AppSettings>(JsonOptions);
        }

        if (!File.Exists(_legacyFile))
            return null;

        var before = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(_legacyFile, cancellationToken).ConfigureAwait(false), JsonOptions);
        if (before is not null)
        {
            await SaveAsync(before, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Took over the settings of {File}, which was left as it is", _legacyFile);
        }

        return before;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = DateTimeOffset.UtcNow;
        var saved = await _store.SaveAsync(WorkspaceObjectEnvelope.CreateJson(SettingsType, SettingsId, "Settings", JsonSerializer.Serialize(new { name = "Settings", settings }, JsonOptions), new WorkspaceObjectMetadata(now, now)), cancellationToken).ConfigureAwait(false);
        if (!saved.Success)
            throw new InvalidOperationException($"The settings could not be kept in the workspace: {saved.Error}");
    }
}
