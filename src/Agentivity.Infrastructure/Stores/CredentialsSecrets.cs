using System.Security.Cryptography;
using System.Text;
using Agentivity.Infrastructure.Http;

namespace Agentivity.Infrastructure.Stores;

/// <summary>
/// How the secrets of the credentials are encrypted: AES-GCM with the installation's key (<c>v2:</c> in front), the format every
/// version since the first ones writes. What the first versions wrote — AES with a key derived from the machine name, or even the
/// secret in clear — is still read, so that it can be encrypted again.
/// </summary>
public sealed class CredentialsSecrets(byte[] key)
{
    /// <summary>Marks a secret written in the current format.</summary>
    public const string CurrentFormat = "v2:";

    // The key the first versions derived from the machine name: only kept to read (and then re-encrypt) what they stored.
    private static readonly byte[] LegacyKey = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "_flows_agency_credentials"));

    private readonly byte[] _key = key is { Length: 32 } ? key : throw new ArgumentException("A credentials key is 32 bytes.", nameof(key));

    /// <summary>The key made from a phrase (the <c>AGENTIVITY_ENCRYPTION_KEY</c> variable): any text, hashed into a key.</summary>
    public static CredentialsSecrets FromPhrase(string phrase) => new(SHA256.HashData(Encoding.UTF8.GetBytes(phrase)));

    public static bool IsCurrent(string? stored) => stored is not null && stored.StartsWith(CurrentFormat, StringComparison.Ordinal);

    public string Encrypt(string plainText)
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

    /// <summary>Reads a secret of the current format with this key; false when the key is not the one it was encrypted with.</summary>
    public bool TryDecryptCurrent(string stored, out string plain)
    {
        plain = string.Empty;
        try
        {
            var data = Convert.FromBase64String(stored[CurrentFormat.Length..]);
            var nonceSize = AesGcm.NonceByteSizes.MaxSize;
            var tagSize = AesGcm.TagByteSizes.MaxSize;
            var bytes = new byte[data.Length - nonceSize - tagSize];
            using var aes = new AesGcm(_key, tagSize);
            aes.Decrypt(data.AsSpan(0, nonceSize), data.AsSpan(nonceSize + tagSize), data.AsSpan(nonceSize, tagSize), bytes);
            plain = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The plain text of a stored secret; empty — never the stored text — when it cannot be read (<paramref name="unreadable"/>):
    /// encrypted with another key, or for another machine by a first version.
    /// </summary>
    public string Decrypt(string stored, out bool unreadable)
    {
        unreadable = false;
        if (string.IsNullOrEmpty(stored))
            return stored;

        if (IsCurrent(stored))
        {
            if (TryDecryptCurrent(stored, out var plain))
                return plain;
            unreadable = true;
            return string.Empty;
        }

        if (TryDecryptLegacy(stored, out var legacy))
            return legacy;

        unreadable = LooksLikeEncrypted(stored);
        return unreadable ? string.Empty : stored;
    }

    /// <summary>
    /// A secret as it is kept from now on: one of the current format as it is; one a first version wrote, encrypted again while it
    /// can still be read; one that cannot be read any more (encrypted for another machine) as it is — it has to be entered again.
    /// </summary>
    public string Keep(string stored)
    {
        if (string.IsNullOrEmpty(stored) || IsCurrent(stored))
            return stored;
        if (TryDecryptLegacy(stored, out var legacy))
            return Encrypt(legacy);
        return LooksLikeEncrypted(stored) ? stored : Encrypt(stored);
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
}

/// <summary>The credentials are kept, but not the key that reads their secrets: no new key is made, which would lose them for good.</summary>
public sealed class CredentialsKeyMissingException(string message) : InvalidOperationException(message), IUserFacingError
{
    public int HttpStatus => 500;
    public string Code => "credentials_key_missing";
    public string UserMessage => Message;
}

/// <summary>The credentials kept in files by a version of before cannot be taken over as they are; nothing was written, and they were left as they were.</summary>
public sealed class CredentialsTakeOverException(string message, Exception? inner = null) : InvalidOperationException(message, inner), IUserFacingError
{
    public int HttpStatus => 500;
    public string Code => "credentials_takeover_failed";
    public string UserMessage => Message;
}
