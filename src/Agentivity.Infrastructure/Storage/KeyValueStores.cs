using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Storage;

/// <summary>
/// The key-value stores of the Storage nodes (the File Storage resource), a domain of the workspace: each value kept with its store
/// and its key by the workspace module that is loaded. A store is asked for by its name, never by a place on the disk
/// (<see cref="NameOf"/>): a workflow — or a Template imported from elsewhere — reaches nothing else, wherever its
/// <c>file_path</c> points. A path given before names the store by its file name (<c>_data/storage/data.json</c> → <c>data</c>).
/// <para>
/// The file a store was kept in before (<c>_data/storage/&lt;name&gt;.json</c>) is taken over once, the first time the store is
/// opened, and left as it was. A file that cannot be read is not taken over (and is tried again at the next start).
/// </para>
/// </summary>
public sealed class KeyValueStores
{
    public const string EntryType = "storage.entry";
    public const string TakeOverType = "storage.takeover";
    public const string DefaultName = "data";

    /// <summary>The object types kept with the values, and those kept apart (some workspace modules list a domain whole).</summary>
    public static readonly IReadOnlyList<string> EntryObjectTypes = [EntryType];
    public static readonly IReadOnlyList<string> TakeOverObjectTypes = [TakeOverType];

    private static readonly Regex Valid = new("^[A-Za-z0-9 _.-]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions LegacyOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IWorkspaceObjectStore _entries;
    private readonly IWorkspaceObjectStore _takeOvers;
    private readonly string _legacyFolder;
    private readonly ILogger<KeyValueStores> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _takenOver = new(StringComparer.OrdinalIgnoreCase);

    public KeyValueStores(IWorkspaceObjectStore entries, IWorkspaceObjectStore takeOvers, string legacyFolder, ILogger<KeyValueStores> logger)
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _takeOvers = takeOvers ?? throw new ArgumentNullException(nameof(takeOvers));
        _legacyFolder = legacyFolder ?? throw new ArgumentNullException(nameof(legacyFolder));
        _logger = logger;
    }

    /// <summary>
    /// The name of a store: the name given, or — for a path given before — its file name without <c>.json</c>; <c>data</c> when
    /// none is given. Letters, digits, spaces, <c>-</c>, <c>_</c> and <c>.</c> only. Throws <see cref="KeyValueStoreNameException"/>.
    /// </summary>
    public static string NameOf(string? nameOrPath)
    {
        var given = (nameOrPath ?? string.Empty).Trim().Replace('\\', '/');
        if (given.Length == 0)
            return DefaultName;

        var name = given[(given.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^".json".Length];

        if (!Valid.IsMatch(name) || name.Trim('.').Length == 0)
            throw new KeyValueStoreNameException($"'{nameOrPath}' does not name a store: a store is named with letters, digits, spaces, - _ and . (like data or site-cache).");

        return name;
    }

    public IStorageProvider Open(string? nameOrPath) => new Store(this, NameOf(nameOrPath));

    private Task TakeOverOnceAsync(string name) => _takenOver.GetOrAdd(name, n => new Lazy<Task>(() => TakeOverAsync(n))).Value;

    private async Task TakeOverAsync(string name)
    {
        var markerId = name.ToLowerInvariant();
        if ((await _takeOvers.GetAsync(TakeOverType, markerId).ConfigureAwait(false)).Success)
            return;

        var file = Path.Combine(_legacyFolder, name + ".json");
        var count = 0;
        if (File.Exists(file))
        {
            Dictionary<string, LegacyEntry>? kept;
            try
            {
                var text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                kept = string.IsNullOrWhiteSpace(text) ? [] : JsonSerializer.Deserialize<Dictionary<string, LegacyEntry>>(text, LegacyOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "The store file {File} is not valid JSON: it was not taken over, and was left as it is", file);
                return;
            }

            foreach (var (key, entry) in kept ?? [])
            {
                var created = entry.CreatedAt == default ? File.GetLastWriteTimeUtc(file) : entry.CreatedAt;
                await KeepAsync(name, key, entry.Value ?? string.Empty, created, entry.UpdatedAt < created ? created : entry.UpdatedAt, entry.ExpiresAt, entry.Metadata).ConfigureAwait(false);
                count++;
            }
        }

        var payload = new JsonObject { ["name"] = $"Store {name} taken over", ["store"] = name, ["file"] = file, ["items"] = count };
        var marked = await _takeOvers.SaveAsync(WorkspaceObjectEnvelope.CreateJson(TakeOverType, markerId, $"Store {name} taken over", payload.ToJsonString(), new WorkspaceObjectMetadata(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))).ConfigureAwait(false);
        if (!marked.Success)
            throw new InvalidOperationException($"The store {name} could not be kept in the workspace: {marked.Error}");

        if (count > 0)
            _logger.LogInformation("Took over {Count} value(s) of the store {Name} from {File}, which was left as it is", count, name, file);
    }

    /// <summary>The id a value is kept under: its store (whatever its case) and its key, with nothing a file name cannot have.</summary>
    private static string IdOf(string name, string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant() + "\n" + key)));

    private async Task KeepAsync(string name, string key, string value, DateTimeOffset createdAt, DateTimeOffset updatedAt, DateTimeOffset? expiresAt, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["name"] = key,
            ["store"] = name,
            ["key"] = key,
            ["value"] = value,
            ["expiresAt"] = expiresAt,
            ["metadata"] = JsonSerializer.SerializeToNode(metadata ?? new Dictionary<string, string>()),
        };
        var saved = await _entries.SaveAsync(WorkspaceObjectEnvelope.CreateJson(EntryType, IdOf(name, key), key, payload.ToJsonString(), new WorkspaceObjectMetadata(createdAt, updatedAt)), cancellationToken).ConfigureAwait(false);
        if (!saved.Success)
            throw new InvalidOperationException($"The value '{key}' of the store {name} could not be kept in the workspace: {saved.Error}");
    }

    private sealed record Kept(string Key, string Value, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt)
    {
        public bool Expired => ExpiresAt is { } at && at < DateTimeOffset.UtcNow;

        public static Kept? Of(WorkspaceObjectEnvelope envelope, string name)
        {
            var payload = JsonNode.Parse(envelope.Payload);
            if (payload?["key"]?.GetValue<string>() is not { } key || !string.Equals(payload["store"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                return null;

            return new Kept(key, payload["value"]?.GetValue<string>() ?? string.Empty, payload["expiresAt"]?.GetValue<DateTimeOffset?>(), envelope.Metadata.CreatedAtUtc);
        }
    }

    private sealed class LegacyEntry
    {
        public string? Value { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>One store, opened by its name.</summary>
    private sealed class Store(KeyValueStores stores, string name) : IStorageProvider
    {
        public string ProviderName => "File";

        public async ValueTask<StorageResult<string>> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            var kept = await FindAsync(key, cancellationToken).ConfigureAwait(false);
            if (kept is null)
                return StorageResult<string>.Fail($"Key '{key}' not found");

            if (kept.Expired)
            {
                await stores._entries.DeleteAsync(EntryType, IdOf(name, key), cancellationToken).ConfigureAwait(false);
                return StorageResult<string>.Fail($"Key '{key}' has expired");
            }

            return StorageResult<string>.Ok(kept.Value);
        }

        public async ValueTask<StorageResult> WriteAsync(string key, string value, StorageWriteOptions? options = null, CancellationToken cancellationToken = default)
        {
            var kept = await FindAsync(key, cancellationToken).ConfigureAwait(false);
            if (kept is not null && !kept.Expired && options?.Overwrite == false)
                return StorageResult.Fail($"Key '{key}' already exists and overwrite is disabled");

            var now = DateTimeOffset.UtcNow;
            await stores.KeepAsync(name, key, value, now, now, options?.Expiration is { } expiration ? now.Add(expiration) : null, options?.Metadata, cancellationToken).ConfigureAwait(false);
            return StorageResult.Ok();
        }

        public async ValueTask<StorageResult> DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (await FindAsync(key, cancellationToken).ConfigureAwait(false) is null)
                return StorageResult.Fail($"Key '{key}' not found");

            var deleted = await stores._entries.DeleteAsync(EntryType, IdOf(name, key), cancellationToken).ConfigureAwait(false);
            return deleted.Success ? StorageResult.Ok() : StorageResult.Fail(deleted.Error ?? $"Key '{key}' could not be deleted");
        }

        public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => await FindAsync(key, cancellationToken).ConfigureAwait(false) is { Expired: false };

        public async ValueTask<IReadOnlyList<string>> ListKeysAsync(string? pattern = null, CancellationToken cancellationToken = default)
        {
            await stores.TakeOverOnceAsync(name).ConfigureAwait(false);
            var matches = string.IsNullOrWhiteSpace(pattern) ? null : new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
            return (await stores._entries.ListAsync(EntryType, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Select(e => Kept.Of(e, name))
                .OfType<Kept>()
                .Where(k => !k.Expired && (matches is null || matches.IsMatch(k.Key)))
                .OrderBy(k => k.CreatedAt)
                .ThenBy(k => k.Key, StringComparer.Ordinal)
                .Select(k => k.Key)
                .ToList();
        }

        private async Task<Kept?> FindAsync(string key, CancellationToken cancellationToken)
        {
            ValidateKey(key);
            await stores.TakeOverOnceAsync(name).ConfigureAwait(false);
            var kept = await stores._entries.GetAsync(EntryType, IdOf(name, key), cancellationToken).ConfigureAwait(false);
            return kept.Success ? Kept.Of(kept.Value!, name) : null;
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or whitespace", nameof(key));
            if (key.Length > 255)
                throw new ArgumentException("Key cannot exceed 255 characters", nameof(key));
        }
    }
}

/// <summary>A store was asked for by something that is not a store's name.</summary>
public sealed class KeyValueStoreNameException(string message) : ArgumentException(message), IUserFacingError
{
    public int HttpStatus => 400;
    public string Code => "storage_name_invalid";
    public string UserMessage => Message;
}
