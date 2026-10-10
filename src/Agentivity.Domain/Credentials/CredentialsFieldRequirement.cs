namespace Agentivity.Domain.Credentials;

/// <summary>
/// Describes a required or optional field for credentials.
/// </summary>
public sealed record CredentialsFieldRequirement
{
    public required string Key { get; init; }
    public CredentialsFieldType Type { get; init; } = CredentialsFieldType.Text;
    public bool Required { get; init; } = true;
    public string? Label { get; init; }
    public string? Placeholder { get; init; }
    public string? HelpText { get; init; }
    public string? DefaultValue { get; init; }
}
