using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// File-based credentials store with encryption for secret fields.
/// Reads storage path from Credentials:StoragePath in configuration.
/// Persistence (single-file, whole-list read-modify-write, thread-safety) is inherited from
/// <see cref="AbstractSingleFileListStore{TRecord}"/> — this class only owns the credentials-specific
/// business logic (id generation, per-field encryption/masking, template-aware field completion)
/// layered on top of the inherited <see cref="AddAsync"/>/<see cref="FindAsync"/>/<see cref="FindWhereAsync"/>
/// primitives.
/// </summary>
public sealed class FileCredentialsRepository : AbstractSingleFileListStore<Credentials>, ICredentialsRepository
{
    /// <summary>Marks a secret written in the current format (AES-GCM, with the installation's own key).</summary>
    private const string CurrentFormat = "v2:";

    /// <summary>Overrides the key file: any text, hashed into the key. Set it to keep the secrets out of the data folder.</summary>
    public const string EncryptionKeyVariable = "AGENTIVITY_ENCRYPTION_KEY";

    // The key the first versions derived from the machine name: only kept to read (and then re-encrypt) what they stored.
    private static readonly byte[] LegacyKey = DeriveLegacyKey();

    private readonly byte[] _key;
    private readonly ILogger<FileCredentialsRepository> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public FileCredentialsRepository(IOptions<CredentialsOptions> options, ILogger<FileCredentialsRepository> logger)
        : base(options.Value.StoragePath!, c => c.Id, SerializerOptions)
    {
        _logger = logger;
        _logger.LogInformation("Credentials store using path: {FilePath}", FilePath);
        _key = LoadOrCreateKey(Path.GetDirectoryName(Path.GetFullPath(FilePath)) ?? ".");
        MigrateLegacyWrapperFormat();
        MigrateSecretsToCurrentFormat();
    }

    /// <summary>
    /// The key that encrypts the secrets: the one in <see cref="EncryptionKeyVariable"/> when set, otherwise a random key generated
    /// once and kept in a file next to the credentials. It belongs to the installation — not to the machine or the container,
    /// whose name changes — so a recreated container reading the same data folder still reads its secrets.
    /// </summary>
    private static byte[] LoadOrCreateKey(string directory)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EncryptionKeyVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(fromEnvironment));
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".credentials.key");
        if (File.Exists(path))
        {
            try
            {
                var existing = Convert.FromBase64String(File.ReadAllText(path).Trim());
                if (existing.Length == 32)
                {
                    return existing;
                }
            }
            catch (FormatException)
            {
                // Falls through to the error below.
            }

            throw new InvalidOperationException($"The credentials key file '{path}' is not valid. Restore it from a backup, or delete it (the stored secrets will then have to be entered again).");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }

    /// <summary>
    /// Secrets stored by the first versions (AES with a key derived from the machine name, or even in clear) are re-encrypted with the
    /// current key at startup, while they can still be read. A secret that cannot be read any more (the machine name changed)
    /// is left as it is and reported: the key has to be entered again.
    /// </summary>
    private void MigrateSecretsToCurrentFormat()
    {
        if (!File.Exists(FilePath))
            return;

        try
        {
            var credentials = JsonSerializer.Deserialize<List<Credentials>>(File.ReadAllText(FilePath), SerializerOptions);
            if (credentials is null)
                return;

            var migrated = 0;
            var migratedCredentials = credentials
                .Select(credential =>
                {
                    var template = CredentialsTemplateRegistry.Get(credential.TemplateId);
                    var fields = credential
                        .Fields.Select(field =>
                        {
                            if (!IsSecretField(field.Key, template) || string.IsNullOrEmpty(field.Value) || field.Value.StartsWith(CurrentFormat, StringComparison.Ordinal))
                                return field;

                            if (TryDecryptLegacy(field.Value, out var plain))
                            {
                                migrated++;
                                return field with { Value = Encrypt(plain) };
                            }

                            if (LooksLikeEncrypted(field.Value))
                            {
                                _logger.LogWarning("The secret '{Field}' of credentials {Id} cannot be read any more (it was encrypted for another machine). Enter it again.", field.Key, credential.Id);
                                return field;
                            }

                            // Stored in clear by an early version.
                            migrated++;
                            return field with { Value = Encrypt(field.Value) };
                        })
                        .ToList();
                    return credential with { Fields = fields };
                })
                .ToList();

            if (migrated > 0)
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(migratedCredentials, SerializerOptions));
                _logger.LogInformation("Re-encrypted {Count} stored secret(s) with the installation key.", migrated);
            }
        }
        catch (JsonException)
        {
            // Not a credentials list: nothing to migrate.
        }
    }

    /// <summary>
    /// One-time, best-effort migration: the store used to persist a <c>{ "Credentials": [...] }</c>
    /// wrapper object; <see cref="AbstractSingleFileListStore{TRecord}"/> reads/writes a bare JSON
    /// array. Rewrites an existing wrapper-shaped file in place before any other access — a no-op
    /// once migrated (bare array fails the initial object-with-"Credentials"-array shape check).
    /// </summary>
    private void MigrateLegacyWrapperFormat()
    {
        if (!File.Exists(FilePath))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return;

            // TryGetProperty is case-sensitive; the legacy wrapper was written with a camelCase
            // naming policy ("credentials"), so match the property by name regardless of case.
            var credentialsProperty = document.RootElement.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "credentials", StringComparison.OrdinalIgnoreCase));
            if (credentialsProperty.Value.ValueKind != JsonValueKind.Array)
                return;
            var credentialsElement = credentialsProperty.Value;

            var credentials = JsonSerializer.Deserialize<List<Credentials>>(credentialsElement.GetRawText(), SerializerOptions) ?? [];
            File.WriteAllText(FilePath, JsonSerializer.Serialize(credentials, SerializerOptions));
            _logger.LogInformation("Migrated credentials store from the legacy wrapper format to a bare array ({Count} entries).", credentials.Count);
        }
        catch (JsonException)
        {
            // Not the legacy wrapper shape (or already the bare array) — nothing to migrate.
        }
    }

    public async Task<IReadOnlyList<Credentials>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await FindAllAsync(cancellationToken).ConfigureAwait(false);
        return all.Select(MaskSecrets).ToList();
    }

    public async Task<IReadOnlyList<Credentials>> GetByTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var matches = await FindWhereAsync(c => string.Equals(c.TemplateId, templateId, StringComparison.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
        return matches.Select(MaskSecrets).ToList();
    }

    public async Task<Credentials?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var cred = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        return cred is null ? null : DecryptSecrets(cred);
    }

    public async Task<Credentials> CreateAsync(string name, string templateId, string? description, List<CredentialsField> fields, CancellationToken cancellationToken = default)
    {
        var template = CredentialsTemplateRegistry.Get(templateId) ?? throw new ArgumentException($"Template '{templateId}' not found.", nameof(templateId));

        // Ensure all template fields are present (use provided values or defaults); the mask is never a value to store.
        var completeFields = EnsureAllTemplateFields(fields, template).Select(f => IsSecretField(f.Key, template) && f.Value == CredentialsMasking.MaskedValue ? f with { Value = string.Empty } : f).ToList();

        var cred = new Credentials
        {
            Id = $"cred_{Guid.NewGuid():N}",
            Name = name,
            TemplateId = templateId,
            Description = description,
            Fields = EncryptSecretFields(completeFields, template),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await AddAsync(cred, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created credentials {Id} ({Name}) with template {TemplateId}", cred.Id, name, templateId);
        return cred with { Fields = completeFields };
    }

    public async Task<Credentials?> UpdateAsync(string id, string name, string? description, List<CredentialsField> fields, CancellationToken cancellationToken = default)
    {
        List<CredentialsField>? completeFields = null;

        var updated = await RunExclusiveAsync<Credentials?>(all =>
        {
            var index = all.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return (all, null);

            var existing = all[index];
            var template = CredentialsTemplateRegistry.Get(existing.TemplateId);

            // Ensure all template fields are present
            completeFields = EnsureAllTemplateFields(fields, template);

            var updatedFields = completeFields
                .Select(f =>
                {
                    var isSecret = IsSecretField(f.Key, template);
                    if (isSecret && f.Value == CredentialsMasking.MaskedValue)
                    {
                        // Keep existing encrypted value
                        return existing.Fields.FirstOrDefault(ef => string.Equals(ef.Key, f.Key, StringComparison.OrdinalIgnoreCase)) ?? f;
                    }
                    return isSecret ? f with { Value = Encrypt(f.Value) } : f;
                })
                .ToList();

            var newRecord = existing with { Name = name, Description = description, Fields = updatedFields };
            all[index] = newRecord;
            return (all, newRecord);
        }, cancellationToken).ConfigureAwait(false);

        if (updated is null)
            return null;

        _logger.LogInformation("Updated credentials {Id} ({Name})", id, name);
        return updated with { Fields = completeFields! };
    }

    async Task<bool> ICredentialsRepository.DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var removed = await RunExclusiveAsync(all => (all, all.RemoveAll(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) > 0), cancellationToken).ConfigureAwait(false);
        if (removed)
            _logger.LogInformation("Deleted credentials {Id}", id);
        return removed;
    }

    private static Credentials MaskSecrets(Credentials cred) => CredentialsMasking.Mask(cred);

    private Credentials DecryptSecrets(Credentials cred)
    {
        var template = CredentialsTemplateRegistry.Get(cred.TemplateId);
        return cred with { Fields = cred.Fields.Select(f => IsSecretField(f.Key, template) ? f with { Value = Decrypt(f.Value) } : f).ToList() };
    }

    private List<CredentialsField> EncryptSecretFields(List<CredentialsField> fields, CredentialsTemplate template) => fields.Select(f => IsSecretField(f.Key, template) ? f with { Value = Encrypt(f.Value) } : f).ToList();

    private static bool IsSecretField(string key, CredentialsTemplate? template) => CredentialsMasking.IsSecretField(key, template);

    /// <summary>
    /// Ensures all template fields are present in the provided fields list.
    /// Missing fields are added with their default value (or empty string).
    /// </summary>
    private static List<CredentialsField> EnsureAllTemplateFields(List<CredentialsField> providedFields, CredentialsTemplate? template)
    {
        if (template is null)
            return providedFields;

        var result = new List<CredentialsField>();
        var providedDict = providedFields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var templateField in template.Fields)
        {
            if (providedDict.TryGetValue(templateField.Key, out var providedField))
            {
                // Use the provided value
                result.Add(providedField);
            }
            else
            {
                // Add missing field with default value
                result.Add(new CredentialsField { Key = templateField.Key, Value = templateField.DefaultValue ?? string.Empty });
            }
        }

        return result;
    }

    private string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        return CurrentFormat + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The plain text of a stored secret; empty when it cannot be read (wrong key), never the stored text.</summary>
    private string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;

        if (stored.StartsWith(CurrentFormat, StringComparison.Ordinal))
        {
            try
            {
                var data = Convert.FromBase64String(stored[CurrentFormat.Length..]);
                var nonceSize = AesGcm.NonceByteSizes.MaxSize;
                var tagSize = AesGcm.TagByteSizes.MaxSize;
                var plain = new byte[data.Length - nonceSize - tagSize];
                using var aes = new AesGcm(_key, tagSize);
                aes.Decrypt(data.AsSpan(0, nonceSize), data.AsSpan(nonceSize + tagSize), data.AsSpan(nonceSize, tagSize), plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                _logger.LogWarning("A stored secret could not be decrypted with this installation's key (was the key file or {Variable} changed?). Enter it again.", EncryptionKeyVariable);
                return string.Empty;
            }
        }

        // Written by an early version: readable with the machine-name key, or stored in clear. Encrypted text that cannot be read any
        // more is never handed out as if it were the secret.
        if (TryDecryptLegacy(stored, out var legacy))
            return legacy;

        return LooksLikeEncrypted(stored) ? string.Empty : stored;
    }

    private static bool TryDecryptLegacy(string cipherText, out string plain)
    {
        plain = string.Empty;
        try
        {
            var fullCipher = Convert.FromBase64String(cipherText);
            using var aes = Aes.Create();
            aes.Key = LegacyKey;
            var iv = new byte[aes.BlockSize / 8];
            if (fullCipher.Length <= iv.Length)
                return false;

            var cipher = new byte[fullCipher.Length - iv.Length];
            Array.Copy(fullCipher, iv, iv.Length);
            Array.Copy(fullCipher, iv.Length, cipher, 0, cipher.Length);
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            plain = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True for text that looks like something encrypted (a block-sized base64 payload), as opposed to a key typed in clear.</summary>
    private static bool LooksLikeEncrypted(string value)
    {
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out var written) && written >= 32 && written % 16 == 0;
    }

    private static byte[] DeriveLegacyKey()
    {
        var seed = Environment.MachineName + "_flows_agency_credentials";
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
    }
}
