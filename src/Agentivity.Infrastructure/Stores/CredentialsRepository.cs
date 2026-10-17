using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// The credentials, a domain of the workspace: each one kept by the workspace module the edition loads — in the SQLite file for
/// the Community —, its secrets encrypted (<see cref="CredentialsSecrets"/>), never in clear. The key that encrypts them is kept
/// in the workspace too, unless the operator gives one (<see cref="EncryptionKeyVariable"/>), which is then never kept.
/// <para>
/// An installation of before kept them in a file (<c>Credentials:StoragePath</c>) with its key in a file beside it: the first time,
/// both are taken over exactly as they were — the secrets as they were encrypted, the key as it was —, checked first (a key that
/// does not read the secrets stops it, nothing written), then never again. The files are left as they were. A key is made only
/// when there is nothing to read with it: no key is ever made over credentials, which would lose their secrets for good.
/// </para>
/// </summary>
public sealed class CredentialsRepository : ICredentialsRepository
{
    public const string CredentialType = "credential";
    public const string KeyType = "credential.key";
    public const string KeyId = "installation";

    /// <summary>Marks that the files of before were taken over (or that there were none).</summary>
    public const string TakeOverType = "credential.takeover";

    /// <summary>The object types of the connections' domain.</summary>
    public static readonly IReadOnlyList<string> ObjectTypes = [CredentialType];

    /// <summary>
    /// The object types of the domain beside it — the key and the mark of the take-over —, apart from the connections so that a
    /// workspace module that keeps a domain's objects together (Json.Advanced: one file each, in one folder) never lists them as
    /// connections.
    /// </summary>
    public static readonly IReadOnlyList<string> KeyObjectTypes = [KeyType, TakeOverType];

    /// <summary>The key and the mark of what was taken over, told apart by their ids where they were kept without their types.</summary>
    public static readonly WorkspaceDomainTypes KeyDomainTypes = new(KeyObjectTypes, static (id, _) => string.Equals(id, KeyId, StringComparison.OrdinalIgnoreCase) ? KeyType : string.Equals(id, TakeOverId, StringComparison.OrdinalIgnoreCase) ? TakeOverType : null);

    /// <summary>Gives the key in place of the one kept in the workspace: any text, hashed into the key. Never kept.</summary>
    public const string EncryptionKeyVariable = "AGENTIVITY_ENCRYPTION_KEY";

    private const string TakeOverId = "files";
    private const string LegacyKeyFileName = ".credentials.key";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IWorkspaceObjectStore _store;
    private readonly IWorkspaceObjectStore _keyStore;
    private readonly string _legacyFile;
    private readonly string? _environmentKey;
    private readonly ILogger<CredentialsRepository> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CredentialsSecrets? _secrets;

    /// <param name="store">Where the connections are kept.</param>
    /// <param name="keyStore">
    /// Where their key and the mark of the take-over are kept: another domain, never the connections' own (the app's routing store
    /// routes each type to its domain).
    /// </param>
    /// <param name="environmentKey">The key the operator gives (<see cref="EncryptionKeyVariable"/>), or null.</param>
    public CredentialsRepository(IWorkspaceObjectStore store, IWorkspaceObjectStore keyStore, CredentialsOptions options, ILogger<CredentialsRepository> logger, string? environmentKey)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _legacyFile = PathResolver.Resolve(string.IsNullOrWhiteSpace(options.StoragePath) ? "_data/credentials.json" : options.StoragePath);
        _logger = logger;
        _environmentKey = string.IsNullOrWhiteSpace(environmentKey) ? null : environmentKey;
    }

    public async Task<IReadOnlyList<Credentials>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        return (await ListAsync(cancellationToken).ConfigureAwait(false)).Select(CredentialsMasking.Mask).ToList();
    }

    public async Task<IReadOnlyList<Credentials>> GetByTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        return (await ListAsync(cancellationToken).ConfigureAwait(false)).Where(c => string.Equals(c.TemplateId, templateId, StringComparison.OrdinalIgnoreCase)).Select(CredentialsMasking.Mask).ToList();
    }

    public async Task<Credentials?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var secrets = await ReadyAsync(cancellationToken).ConfigureAwait(false);
        var kept = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        return kept is null ? null : Decrypted(kept, secrets);
    }

    public async Task<Credentials> CreateAsync(string name, string templateId, string? description, List<CredentialsField> fields, CancellationToken cancellationToken = default)
    {
        var secrets = await ReadyAsync(cancellationToken).ConfigureAwait(false);
        var template = CredentialsTemplateRegistry.Get(templateId) ?? throw new ArgumentException($"Template '{templateId}' not found.", nameof(templateId));

        // Every field of the template is there (the value given, or its default); the mask is never a value to keep.
        var completeFields = WithAllTemplateFields(fields, template).Select(f => IsSecret(f.Key, template) && f.Value == CredentialsMasking.MaskedValue ? f with { Value = string.Empty } : f).ToList();
        var credentials = new Credentials
        {
            Id = $"cred_{Guid.NewGuid():N}",
            Name = name,
            TemplateId = templateId,
            Description = description,
            Fields = completeFields.Select(f => IsSecret(f.Key, template) ? f with { Value = secrets.Encrypt(f.Value) } : f).ToList(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await SaveAsync(credentials, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created credentials {Id} ({Name}) with template {TemplateId}", credentials.Id, name, templateId);
        return credentials with { Fields = completeFields };
    }

    public async Task<Credentials?> UpdateAsync(string id, string name, string? description, List<CredentialsField> fields, CancellationToken cancellationToken = default)
    {
        var secrets = await ReadyAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindAsync(id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                return null;

            var template = CredentialsTemplateRegistry.Get(existing.TemplateId);
            var completeFields = WithAllTemplateFields(fields, template);
            var kept = completeFields
                .Select(f =>
                {
                    if (!IsSecret(f.Key, template))
                        return f;
                    // The mask sent back keeps the secret as it was.
                    return f.Value == CredentialsMasking.MaskedValue ? existing.Fields.FirstOrDefault(e => string.Equals(e.Key, f.Key, StringComparison.OrdinalIgnoreCase)) ?? f : f with { Value = secrets.Encrypt(f.Value) };
                })
                .ToList();

            var updated = existing with { Name = name, Description = description, Fields = kept };
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated credentials {Id} ({Name})", id, name);
            return updated with { Fields = completeFields };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await ReadyAsync(cancellationToken).ConfigureAwait(false);
        var removed = (await _store.DeleteAsync(CredentialType, id, cancellationToken).ConfigureAwait(false)).Success;
        if (removed)
            _logger.LogInformation("Deleted credentials {Id}", id);
        return removed;
    }

    // ── The key, and what was kept before ───────────────────────────────────────────────────

    private async ValueTask<CredentialsSecrets> ReadyAsync(CancellationToken cancellationToken)
    {
        if (_secrets is { } ready)
            return ready;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_secrets is null)
            {
                if (!(await _keyStore.GetAsync(TakeOverType, TakeOverId, cancellationToken).ConfigureAwait(false)).Success)
                {
                    await TakeOverFilesAsync(cancellationToken).ConfigureAwait(false);
                }

                _secrets = await KeyAsync(cancellationToken).ConfigureAwait(false);
            }

            return _secrets;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CredentialsSecrets> KeyAsync(CancellationToken cancellationToken)
    {
        if (_environmentKey is not null)
            return CredentialsSecrets.FromPhrase(_environmentKey);

        var kept = await _keyStore.GetAsync(KeyType, KeyId, cancellationToken).ConfigureAwait(false);
        if (kept.Success)
        {
            return KeyOf(kept.Value!.Payload) is { } key
                ? new CredentialsSecrets(key)
                : throw new CredentialsKeyMissingException("The key that reads the secrets of the connections is damaged in the workspace. Restore the workspace from a backup.");
        }

        if ((await _store.ListAsync(CredentialType, new WorkspaceObjectQuery(Take: 1), cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new CredentialsKeyMissingException($"The connections are kept, but not the key that reads their secrets. Restore the workspace from a backup, or give the key with {EncryptionKeyVariable} if it was given that way — no new key is made, which would lose the secrets for good.");
        }

        // Nothing to read: a new installation.
        var made = RandomNumberGenerator.GetBytes(32);
        await SaveKeyAsync(made, cancellationToken).ConfigureAwait(false);
        return new CredentialsSecrets(made);
    }

    private async Task TakeOverFilesAsync(CancellationToken cancellationToken)
    {
        var items = 0;
        if (File.Exists(_legacyFile))
        {
            var before = ReadLegacyFile();
            var keyFile = Path.Combine(Path.GetDirectoryName(_legacyFile) ?? ".", LegacyKeyFileName);
            var (secrets, fileKey) = LegacyKey(keyFile, before);

            // Every secret encrypted with the installation's key must read with the key taken over: one that does not means the
            // key is not theirs, and nothing is written.
            foreach (var credentials in before)
            {
                var template = CredentialsTemplateRegistry.Get(credentials.TemplateId);
                foreach (var field in credentials.Fields.Where(f => IsSecret(f.Key, template) && CredentialsSecrets.IsCurrent(f.Value)))
                {
                    if (!secrets.TryDecryptCurrent(field.Value, out _))
                    {
                        throw new CredentialsTakeOverException($"The connections kept in {_legacyFile} cannot be read with the key {(_environmentKey is null ? keyFile : EncryptionKeyVariable)} (the secret '{field.Key}' of '{credentials.Name}'). Nothing was taken over, and the files were left as they are: put back the key they were encrypted with.");
                    }
                }
            }

            foreach (var credentials in before)
            {
                var template = CredentialsTemplateRegistry.Get(credentials.TemplateId);
                await SaveAsync(credentials with { Fields = credentials.Fields.Select(f => IsSecret(f.Key, template) ? f with { Value = secrets.Keep(f.Value) } : f).ToList() }, cancellationToken).ConfigureAwait(false);
                items++;
            }

            if (fileKey is not null)
            {
                await SaveKeyAsync(fileKey, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Took over {Count} connection(s) from {File}{Key}. The files were left as they are{Delete}",
                items,
                _legacyFile,
                fileKey is null ? string.Empty : " with their key, now kept in the workspace",
                File.Exists(keyFile) ? $": once the connections are checked, {keyFile} can be deleted." : "."
            );
        }

        var marker = WorkspaceObjectEnvelope.CreateJson(TakeOverType, TakeOverId, "Files taken over", JsonSerializer.Serialize(new { name = "Files taken over", file = _legacyFile, items }), new WorkspaceObjectMetadata(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Check(await _keyStore.SaveAsync(marker, cancellationToken).ConfigureAwait(false));
    }

    private List<Credentials> ReadLegacyFile()
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_legacyFile));
            // The first versions wrapped the list: { "credentials": [ ... ] }.
            var list = root is JsonObject wrapper ? wrapper.FirstOrDefault(p => string.Equals(p.Key, "credentials", StringComparison.OrdinalIgnoreCase)).Value : root;
            return list?.Deserialize<List<Credentials>>(SerializerOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new CredentialsTakeOverException($"The connections kept in {_legacyFile} cannot be read ({ex.Message}). Nothing was taken over, and the file was left as it is: repair it and start again.", ex);
        }
    }

    /// <summary>The key the connections of before were encrypted with — given by the operator, or kept in its file — and that file's key, to keep.</summary>
    private (CredentialsSecrets Secrets, byte[]? FileKey) LegacyKey(string keyFile, List<Credentials> before)
    {
        if (_environmentKey is not null)
            return (CredentialsSecrets.FromPhrase(_environmentKey), null);

        if (File.Exists(keyFile))
        {
            return KeyOf(File.ReadAllText(keyFile)) is { } key
                ? (new CredentialsSecrets(key), key)
                : throw new CredentialsTakeOverException($"The key file {keyFile} is not valid. Nothing was taken over, and the files were left as they are: restore it from a backup.");
        }

        var encrypted = before.Any(c => c.Fields.Any(f => CredentialsSecrets.IsCurrent(f.Value)));
        if (encrypted)
        {
            throw new CredentialsTakeOverException($"The connections kept in {_legacyFile} are encrypted, but their key file {keyFile} is missing. Nothing was taken over, and no new key was made: put the key file back (from a backup), or give the key with {EncryptionKeyVariable}.");
        }

        // Nothing encrypted with an installation key: a new one encrypts what the first versions left.
        var made = RandomNumberGenerator.GetBytes(32);
        return (new CredentialsSecrets(made), made);
    }

    private static byte[]? KeyOf(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('{'))
        {
            try
            {
                value = JsonNode.Parse(value)?["key"]?.GetValue<string>() ?? string.Empty;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var bytes = new byte[value.Length];
        return Convert.TryFromBase64String(value, bytes, out var written) && written == 32 ? bytes[..32] : null;
    }

    private async Task SaveKeyAsync(byte[] key, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        Check(await _keyStore.SaveAsync(WorkspaceObjectEnvelope.CreateJson(KeyType, KeyId, "Key of the secrets", JsonSerializer.Serialize(new { name = "Key of the secrets", key = Convert.ToBase64String(key) }), new WorkspaceObjectMetadata(now, now)), cancellationToken).ConfigureAwait(false));
    }

    // ── Kept in the workspace ───────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Credentials>> ListAsync(CancellationToken cancellationToken) =>
        (await _store.ListAsync(CredentialType, cancellationToken: cancellationToken).ConfigureAwait(false)).Select(e => JsonSerializer.Deserialize<Credentials>(e.Payload, SerializerOptions)!).ToList();

    private async Task<Credentials?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var kept = await _store.GetAsync(CredentialType, id, cancellationToken).ConfigureAwait(false);
        return kept.Success ? JsonSerializer.Deserialize<Credentials>(kept.Value!.Payload, SerializerOptions) : null;
    }

    private async Task SaveAsync(Credentials credentials, CancellationToken cancellationToken) =>
        Check(await _store.SaveAsync(WorkspaceObjectEnvelope.CreateJson(CredentialType, credentials.Id, credentials.Name, JsonSerializer.Serialize(credentials, SerializerOptions), new WorkspaceObjectMetadata(credentials.CreatedAt, DateTimeOffset.UtcNow)), cancellationToken).ConfigureAwait(false));

    private static void Check(Agentivity.Infrastructure.Storage.StorageResult result)
    {
        if (!result.Success)
            throw new InvalidOperationException($"The connections could not be kept in the workspace: {result.Error}");
    }

    private Credentials Decrypted(Credentials credentials, CredentialsSecrets secrets)
    {
        var template = CredentialsTemplateRegistry.Get(credentials.TemplateId);
        return credentials with
        {
            Fields = credentials
                .Fields.Select(f =>
                {
                    if (!IsSecret(f.Key, template))
                        return f;
                    var plain = secrets.Decrypt(f.Value, out var unreadable);
                    if (unreadable)
                        _logger.LogWarning("The secret '{Field}' of credentials {Id} cannot be read with this installation's key. Enter it again.", f.Key, credentials.Id);
                    return f with { Value = plain };
                })
                .ToList(),
        };
    }

    private static bool IsSecret(string key, CredentialsTemplate? template) => CredentialsMasking.IsSecretField(key, template);

    /// <summary>Every field of the template: the value given, or the field's default (or nothing).</summary>
    private static List<CredentialsField> WithAllTemplateFields(List<CredentialsField> given, CredentialsTemplate? template)
    {
        if (template is null)
            return given;
        var byKey = given.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        return template.Fields.Select(t => byKey.TryGetValue(t.Key, out var value) ? value : new CredentialsField { Key = t.Key, Value = t.DefaultValue ?? string.Empty }).ToList();
    }
}
