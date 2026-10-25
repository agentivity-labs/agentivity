namespace Agentivity.Domain.Credentials;

/// <summary>
/// Defines a reusable credential template with its structure.
/// </summary>
public sealed record CredentialsTemplate
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// What kind of service the connection is for, in the words a person looks for it by: one of
    /// <see cref="CredentialsCategories"/>. It is how the list of templates is sorted when someone adds a connection.
    /// </summary>
    public string Category { get; init; } = CredentialsCategories.Other;
    public int? Icon { get; init; }
    public string? IconColor { get; init; }
    public required IReadOnlyList<CredentialsFieldRequirement> Fields { get; init; }
}

/// <summary>The families a connection template belongs to. A short, stable list: a template names one of them.</summary>
public static class CredentialsCategories
{
    public const string AiModels = "AI models";
    public const string Messaging = "Email & messaging";
    public const string Web = "Web search & scraping";
    public const string Data = "Data & vector stores";
    public const string Publishing = "Publishing";
    public const string Other = "Other";

    /// <summary>In the order they are shown.</summary>
    public static readonly IReadOnlyList<string> All = [AiModels, Messaging, Web, Data, Publishing, Other];
}
