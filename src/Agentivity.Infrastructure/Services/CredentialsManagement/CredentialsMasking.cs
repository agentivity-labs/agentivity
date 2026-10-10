using Agentivity.Domain.Credentials;
using CredentialModel = Agentivity.Domain.Credentials.Credentials;

namespace Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;

/// <summary>
/// A secret (an API key, a token, a password) is internal to the server: it is typed once, stored encrypted and used by the engine,
/// and it never comes back out — not through the API, not into the Studio. What comes out in its place is a fixed mask, which
/// the update call recognises as "keep the stored value".
/// </summary>
public static class CredentialsMasking
{
    /// <summary>What stands for a stored secret. Sending it back on an update leaves the stored secret untouched.</summary>
    public const string MaskedValue = "••••••••";

    public static bool IsSecretField(string key, CredentialsTemplate? template) => template?.Fields.Any(tf => string.Equals(tf.Key, key, StringComparison.OrdinalIgnoreCase) && tf.Type == CredentialsFieldType.Secret) ?? false;

    /// <summary>The credentials as they may leave the server: every secret replaced by the mask (or by nothing when it holds nothing).</summary>
    public static CredentialModel Mask(CredentialModel credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var template = CredentialsTemplateRegistry.Get(credentials.TemplateId);
        return credentials with { Fields = credentials.Fields.Select(f => IsSecretField(f.Key, template) ? f with { Value = string.IsNullOrEmpty(f.Value) ? string.Empty : MaskedValue } : f).ToList() };
    }
}
