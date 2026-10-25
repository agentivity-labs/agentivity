namespace Agentivity.Infrastructure.Agentic.Library;

/// <param name="DryRun">Only report what would be created: nothing is saved.</param>
/// <param name="ModelConnection">null or "default": the first model connection of the list; "keep": leave the author's connection as it is; otherwise the id of one of the user's own credentials.</param>
/// <param name="IncludeRows">Import the rows of the data tables the Template carries.</param>
/// <param name="Folder">
/// The one place everything goes, as folder names from the top level down (null or empty for the top level). Agents and teams,
/// workflows and data tables each keep their own folders: the path is followed in each library the Template adds to, and made
/// where it is missing.
/// </param>
public sealed record TemplateImportOptions(bool DryRun = true, string? ModelConnection = null, bool IncludeRows = true, IReadOnlyList<string>? Folder = null);

/// <param name="Status">would_create | created</param>
public sealed record TemplateImportItem(string Kind, string OldId, string NewId, string Name, string Status);

/// <param name="Status">
/// kept (the author's connection was left as it is, on request) | nothing_to_adapt (no model node) | no_connection (the user has no model connection yet) |
/// will_adapt (dry run) | adapted | already_using (nothing to change)
/// </param>
public sealed record TemplateImportModel(string Status, LlmReplacementCredential? Credential, int NodesFound, int NodesReplaced, string? Note);

/// <summary>What an import did, or would do. <paramref name="Problems"/> is not empty when nothing was (or could be) imported.</summary>
/// <param name="ToConfigure">Credential families other than the model ones, to set up after the import.</param>
public sealed record TemplateImportResult(
    bool DryRun,
    string TemplateId,
    string Name,
    string Version,
    TemplateRoot? Root,
    IReadOnlyList<TemplateImportItem> Items,
    TemplateImportModel Model,
    IReadOnlyList<string> RequiresCredentials,
    IReadOnlyList<string> ToConfigure,
    IReadOnlyList<TemplateProblem> Problems,
    IReadOnlyList<string> Warnings
)
{
    public bool IsValid => Problems.Count == 0;

    /// <summary>What was (or would be) created without the author's own settings (a path on the server, a connection string), under its new identifier: to fill in before using it.</summary>
    public IReadOnlyList<TemplateToConfigure> ItemsToConfigure { get; init; } = [];
}

/// <summary>Why an import stopped: the chosen model connection does not exist or is not usable, or something could not be saved (what was created is then removed).</summary>
public sealed class TemplateImportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
