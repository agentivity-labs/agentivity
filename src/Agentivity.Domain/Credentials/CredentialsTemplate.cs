namespace Agentivity.Domain.Credentials;

/// <summary>
/// Defines a reusable credential template with its structure.
/// </summary>
public sealed record CredentialsTemplate
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public int? Icon { get; init; }
    public string? IconColor { get; init; }
    public required IReadOnlyList<CredentialsFieldRequirement> Fields { get; init; }
}
