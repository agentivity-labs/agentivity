namespace Agentivity.Domain.Credentials;

public enum CredentialsFieldType
{
    Text,
    Secret,
    Number,
    Boolean,

    /// <summary>A web address (<c>https://…</c> or <c>http://…</c>), such as the address of a provider's API.</summary>
    Url,
}
