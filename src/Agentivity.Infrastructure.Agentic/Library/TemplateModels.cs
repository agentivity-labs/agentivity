using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Agentic.Library;

public sealed record TemplateRoot(LibraryEntityKind Kind, string Id);

public sealed record TemplateAuthor(string Name, string? Url = null, string? Id = null);

/// <param name="DataUri">The picture itself, embedded: <c>data:image/(png|jpeg|webp|svg+xml);base64,...</c>, 200 KB at most.</param>
public sealed record TemplateCoverInput(string? DataUri, string? Alt = null);

/// <summary>What the author says about the Template. Everything but the root may be left out: the export proposes values taken from the root.</summary>
public sealed record TemplateExportRequest(
    TemplateRoot Root,
    string? Id = null,
    string? Version = null,
    string? Name = null,
    string? Tagline = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    string? Language = null,
    TemplateCoverInput? Cover = null,
    TemplateAuthor? Author = null,
    string? License = null,
    bool IncludeRows = true
);

/// <summary>What the Template is made of: one line per entity (<paramref name="Depth"/> 0 is the root).</summary>
public sealed record TemplateContentItem(string Kind, string Id, string Name, string? Role, int Depth);

/// <summary>A reference that could not be followed. <paramref name="Reason"/> is <c>not_found</c> (the Template would be incomplete) or <c>computed_at_run_time</c> (only known when it runs: cannot be checked).</summary>
public sealed record TemplateUnresolved(string FromKind, string FromId, string FromName, string Reference, string Reason);

/// <summary>Something to fix before the file can be produced. <paramref name="Field"/> names what to change, <paramref name="Code"/> is stable.</summary>
public sealed record TemplateProblem(string Field, string Code, string Message);

/// <summary>Values the creation dialog starts from: the root's own text and tags. All of it can be changed by the author.</summary>
public sealed record TemplateSuggestion(string Id, string Name, string Description, IReadOnlyList<string> Tags);

public sealed record TemplateCounts(int Agents, int Teams, int Workflows, int DataTables, int KnowledgeBases = 0);

/// <summary>
/// Something the Template carries without what belongs to the author's own installation (a path on the server, a connection
/// string): the person who imports it fills in <paramref name="Settings"/> before using it.
/// </summary>
public sealed record TemplateToConfigure(string Kind, string Id, string Name, IReadOnlyList<string> Settings);

/// <param name="File">The Template file, or null while there is something to fix (<paramref name="Problems"/>) or the call only asked for a preview.</param>
public sealed record TemplateExportResult(
    TemplateSuggestion Suggestion,
    TemplateCounts Counts,
    IReadOnlyList<TemplateContentItem> Contents,
    IReadOnlyList<TemplateUnresolved> Unresolved,
    IReadOnlyList<string> RequiresCredentials,
    IReadOnlyList<TemplateProblem> Problems,
    long SizeBytes,
    JsonObject? File,
    IReadOnlyList<TemplateToConfigure>? ToConfigure = null
)
{
    public bool IsValid => Problems.Count == 0;

    /// <summary>What travels without the author's own settings, to be filled in after the import.</summary>
    public IReadOnlyList<TemplateToConfigure> ToConfigure { get; init; } = ToConfigure ?? [];
}

/// <summary>Why an export could not even start (the root does not exist).</summary>
public sealed class TemplateExportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
