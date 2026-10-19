using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Templates;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows;

namespace Agentivity.Infrastructure.Agentic.Library;

/// <summary>
/// Packs a use case into one Template file: the root (a team, a workflow or an agent), everything it calls — members, agents,
/// nested teams and workflows, shared data tables with their rows — and what the marketplace needs to present it. The contents
/// are found the way the model replacement finds them: from the root, following every reference level after level, each item once.
/// The format is described in <c>delivery/features/templates/template-format.md</c>.
/// </summary>
public sealed partial class TemplateExporter(
    IEntityRepository<AgentEntity> agents,
    IEntityRepository<TeamEntity> teams,
    IEntityRepository<WorkflowEntity> workflows,
    CredentialsService credentials,
    IEnumerable<ITemplateDataTableSource> dataTables,
    IEnumerable<ITemplateKnowledgeBaseSource>? knowledgeBases = null
)
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxCoverBytes = 200 * 1024;
    public const int MaxRowsPerTable = 1000;
    public const int MaxDataBytes = 2 * 1024 * 1024;
    public const int MaxTags = 12;

    private const string ModelNodePrefix = "ai.llm.model.";
    /// <summary>Every Template is a solution: its root carries this tag (so it shows up as a Solution in the library), and the Template itself does not list it as a tag.</summary>
    private const string SolutionTag = "solution";

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVer();

    [GeneratedRegex(@"^data:image/(?:png|jpeg|webp|svg\+xml);base64,(?<data>[A-Za-z0-9+/=]+)$")]
    private static partial Regex ImageDataUri();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex NotTag();

    [GeneratedRegex(@"(key|token|secret|password|pass)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretFieldName();

    /// <summary>Reads the contents, checks the values and, when everything is in order and <paramref name="preview"/> is false, builds the file.</summary>
    public async Task<TemplateExportResult> ExportAsync(TemplateExportRequest request, bool preview = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Root);

        var collected = await CollectAsync(request.Root, request.IncludeRows, cancellationToken).ConfigureAwait(false);
        var root = collected.Items[0];

        var allCredentials = await credentials.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var families = FamiliesOf(collected, allCredentials);

        var problems = new List<TemplateProblem>();
        var suggestion = Suggest(request, root, collected.RootGoal);
        var values = Validate(request, suggestion, problems);

        var file = BuildFile(request, values, collected, families, request.Root);
        var text = file.ToJsonString();
        var size = Encoding.UTF8.GetByteCount(text);

        if (size > MaxFileBytes)
        {
            problems.Add(new TemplateProblem("file", "file_too_large", $"The Template is {size / 1024 / 1024.0:0.0} MB: the limit is {MaxFileBytes / 1024 / 1024} MB."));
        }

        foreach (var (tableId, tableRows) in collected.Rows)
        {
            if (tableRows.Count > MaxRowsPerTable)
            {
                problems.Add(new TemplateProblem("data", "data_too_large", $"A data table holds {tableRows.Count} rows: a Template carries {MaxRowsPerTable} at most. Leave the rows out (includeRows: false) or trim the table."));
            }
        }

        if (file["data"] is JsonObject dataObject && Encoding.UTF8.GetByteCount(dataObject.ToJsonString()) > MaxDataBytes)
        {
            problems.Add(new TemplateProblem("data", "data_too_large", $"The data tables weigh more than {MaxDataBytes / 1024 / 1024} MB. Leave the rows out (includeRows: false) or trim them."));
        }

        // The list is masked; the real values are read one credential at a time, only to check that none of them is in the content.
        var ownSecrets = new List<string>();
        foreach (var listed in allCredentials)
        {
            var full = await credentials.GetByIdAsync(listed.Id, cancellationToken).ConfigureAwait(false);
            ownSecrets.AddRange((full?.Fields ?? []).Where(f => SecretFieldName().IsMatch(f.Key) && !string.IsNullOrEmpty(f.Value)).Select(f => f.Value));
        }

        foreach (var kind in TemplateSecretScanner.Scan(text, ownSecrets))
        {
            problems.Add(new TemplateProblem("entities", "secret_found", $"{kind} found in the content. Remove it, and revoke it if it was a real one."));
        }

        foreach (var missing in collected.Unresolved.Where(u => u.Reason == "not_found"))
        {
            problems.Add(new TemplateProblem("entities", "reference_not_found", $"'{missing.FromName}' ({missing.FromKind}) points to '{missing.Reference}', which does not exist: the Template would be incomplete."));
        }

        var counts = new TemplateCounts(
            collected.Items.Count(i => i.Kind == "agent"),
            collected.Items.Count(i => i.Kind == "team"),
            collected.Items.Count(i => i.Kind == "workflow"),
            collected.Items.Count(i => i.Kind == "datatable"),
            collected.Items.Count(i => i.Kind == KnowledgeBaseKind)
        );
        var contents = collected.Items.Select(i => new TemplateContentItem(i.Kind, i.Id, i.Name, i.Role, i.Depth)).ToList();

        return new TemplateExportResult(suggestion, counts, contents, collected.Unresolved, families, problems, size, preview || problems.Count > 0 ? null : file, collected.ToConfigure);
    }

    // ── Contents ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The kind of a Knowledge Base among the entities of a Template.</summary>
    public const string KnowledgeBaseKind = TemplateReferences.KnowledgeBaseKind;

    private sealed record Item(string Kind, string Id, string Name, string? Role, int Depth, JsonNode Entry, IReadOnlyList<string> Tags, IReadOnlyList<string>? ToConfigure = null);

    private sealed record Collected(List<Item> Items, List<TemplateUnresolved> Unresolved, Dictionary<string, JsonArray> Rows, ISet<string> CredentialIds, bool HasModelNodes, string? RootGoal, List<TemplateToConfigure> ToConfigure);

    private readonly record struct Pending(string FromKind, string FromId, string FromName, int Depth, LibraryReference Reference);

    private async Task<Collected> CollectAsync(TemplateRoot root, bool includeRows, CancellationToken cancellationToken)
    {
        var items = new List<Item>();
        var unresolved = new List<TemplateUnresolved>();
        var rows = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        var credentialIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, (string FromKind, string FromId, string FromName)>(StringComparer.OrdinalIgnoreCase);
        var bases = new Dictionary<string, (string FromKind, string FromId, string FromName)>(StringComparer.OrdinalIgnoreCase);
        var toConfigure = new List<TemplateToConfigure>();
        var hasModelNodes = false;
        string? rootGoal = null;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(LibraryEntityKind Kind, string Id, int Depth)>();
        queue.Enqueue((root.Kind, root.Id, 0));

        while (queue.Count > 0)
        {
            var (kind, id, depth) = queue.Dequeue();
            if (!visited.Add($"{kind}:{id}"))
            {
                continue;
            }

            var pending = new List<Pending>();
            switch (kind)
            {
                case LibraryEntityKind.Agent:
                {
                    var result = await agents.GetAsync(id, cancellationToken).ConfigureAwait(false);
                    var agent = result.IsSuccess && result.Value is not null ? result.Value : throw new TemplateExportException("entity_not_found", $"Agent '{id}' not found.");
                    items.Add(new Item("agent", agent.Id, agent.Name, agent.Role, depth, JsonSerializer.SerializeToNode(agent, JsonDefaults.Default)!, agent.Tags));
                    InspectGraph(agent.Graph, "agent", agent.Id, agent.Name, depth, pending, tables, bases, credentialIds, ref hasModelNodes);
                    break;
                }
                case LibraryEntityKind.Workflow:
                {
                    var result = await workflows.GetAsync(id, cancellationToken).ConfigureAwait(false);
                    var workflow = result.IsSuccess && result.Value is not null ? result.Value : throw new TemplateExportException("entity_not_found", $"Workflow '{id}' not found.");
                    items.Add(new Item("workflow", workflow.Id, workflow.Name, workflow.Role, depth, JsonSerializer.SerializeToNode(workflow, JsonDefaults.Default)!, workflow.Tags));
                    InspectGraph(workflow, "workflow", workflow.Id, workflow.Name, depth, pending, tables, bases, credentialIds, ref hasModelNodes);
                    break;
                }
                default:
                {
                    var result = await teams.GetAsync(id, cancellationToken).ConfigureAwait(false);
                    var team = result.IsSuccess && result.Value is not null ? result.Value : throw new TemplateExportException("entity_not_found", $"Team '{id}' not found.");
                    items.Add(new Item("team", team.Id, team.Name, team.Role, depth, JsonSerializer.SerializeToNode(team, JsonDefaults.Default)!, team.Tags));
                    if (depth == 0)
                    {
                        rootGoal = team.Goal;
                    }

                    foreach (var member in team.Members)
                    {
                        var memberKind = member.MemberType switch
                        {
                            TeamMemberType.Team => LibraryEntityKind.Team,
                            TeamMemberType.Workflow => LibraryEntityKind.Workflow,
                            _ => LibraryEntityKind.Agent,
                        };
                        pending.Add(new Pending("team", team.Id, team.Name, depth, new LibraryReference([memberKind], member.MemberEntityId)));
                    }

                    break;
                }
            }

            foreach (var reference in pending)
            {
                await ResolveAsync(reference, queue, unresolved, cancellationToken).ConfigureAwait(false);
            }
        }

        // Shared data tables: the schema asset as an entity, the rows apart.
        foreach (var (tableId, from) in tables)
        {
            if (IsComputed(tableId))
            {
                unresolved.Add(new TemplateUnresolved(from.FromKind, from.FromId, from.FromName, tableId, "computed_at_run_time"));
                continue;
            }

            TemplateDataTable? table = null;
            foreach (var candidate in dataTables)
            {
                table = await candidate.GetAsync(tableId, includeRows, cancellationToken).ConfigureAwait(false);
                if (table is not null)
                {
                    break;
                }
            }

            if (table is null)
            {
                // Without the data tables module there is nothing to read them from: the Template would be incomplete just the same.
                unresolved.Add(new TemplateUnresolved(from.FromKind, from.FromId, from.FromName, tableId, "not_found"));
                continue;
            }

            items.Add(new Item("datatable", table.Id, table.Name, null, 1, JsonNode.Parse(table.Entry.GetRawText())!, []));
            if (includeRows && table.Rows.Count > 0)
            {
                rows[table.Id] = new JsonArray(table.Rows.Select(r => JsonNode.Parse(r.GetRawText())).ToArray());
            }
        }

        // Knowledge Bases: their definition, never their documents; what is the author's own is left out, to configure.
        foreach (var (baseId, from) in bases)
        {
            if (IsComputed(baseId))
            {
                unresolved.Add(new TemplateUnresolved(from.FromKind, from.FromId, from.FromName, baseId, "computed_at_run_time"));
                continue;
            }

            TemplateKnowledgeBase? knowledgeBase = null;
            foreach (var candidate in knowledgeBases ?? [])
            {
                knowledgeBase = await candidate.GetAsync(baseId, cancellationToken).ConfigureAwait(false);
                if (knowledgeBase is not null)
                {
                    break;
                }
            }

            if (knowledgeBase is null)
            {
                // Without the Knowledge Base module there is nothing to read it from: the Template would be incomplete just the same.
                unresolved.Add(new TemplateUnresolved(from.FromKind, from.FromId, from.FromName, baseId, "not_found"));
                continue;
            }

            var role = knowledgeBase.Entry.TryGetProperty("role", out var given) && given.ValueKind == JsonValueKind.String ? given.GetString() : null;
            items.Add(new Item(KnowledgeBaseKind, knowledgeBase.Id, knowledgeBase.Name, role, 1, JsonNode.Parse(knowledgeBase.Entry.GetRawText())!, [], knowledgeBase.ToConfigure));
            // The connections it reads with are among what the person needs.
            CollectStrings(knowledgeBase.Entry, credentialIds);
            if (knowledgeBase.ToConfigure.Count > 0)
            {
                toConfigure.Add(new TemplateToConfigure(KnowledgeBaseKind, knowledgeBase.Id, knowledgeBase.Name, knowledgeBase.ToConfigure));
            }
        }

        return new Collected(items, unresolved, rows, credentialIds, hasModelNodes, rootGoal, toConfigure);
    }

    private static void InspectGraph(WorkflowEntity graph, string kind, string id, string name, int depth, List<Pending> pending, Dictionary<string, (string, string, string)> tables, Dictionary<string, (string, string, string)> bases, ISet<string> credentialIds, ref bool hasModelNodes)
    {
        var references = new List<LibraryReference>();
        foreach (var node in graph.Nodes)
        {
            LibraryReferences.Collect(node, references);
            if (LibraryReferences.DataTableId(node) is { } tableId)
            {
                tables.TryAdd(tableId, (kind, id, name));
            }

            if (LibraryReferences.KnowledgeBaseId(node) is { } baseId)
            {
                bases.TryAdd(baseId, (kind, id, name));
            }

            if (node.NodeType.StartsWith(ModelNodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                hasModelNodes = true;
            }

            if (node.Parameters.ValueKind == JsonValueKind.Object)
            {
                CollectStrings(node.Parameters, credentialIds);
            }
        }

        pending.AddRange(references.Select(r => new Pending(kind, id, name, depth, r)));
    }

    private static void CollectStrings(JsonElement element, ISet<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is { Length: > 0 and <= 128 } text)
                {
                    into.Add(text);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectStrings(property.Value, into);
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectStrings(child, into);
                }

                break;
        }
    }

    private static bool IsComputed(string value) => value.Contains("{{", StringComparison.Ordinal) || value.StartsWith('$') || value.StartsWith('=');

    private async Task ResolveAsync(Pending pending, Queue<(LibraryEntityKind Kind, string Id, int Depth)> queue, List<TemplateUnresolved> unresolved, CancellationToken cancellationToken)
    {
        var value = pending.Reference.Value.Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (IsComputed(value))
        {
            unresolved.Add(new TemplateUnresolved(pending.FromKind, pending.FromId, pending.FromName, value, "computed_at_run_time"));
            return;
        }

        var found = await FindAsync(pending.Reference.Kinds, value, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            unresolved.Add(new TemplateUnresolved(pending.FromKind, pending.FromId, pending.FromName, value, "not_found"));
            return;
        }

        queue.Enqueue((found.Value.Kind, found.Value.Id, pending.Depth + 1));
    }

    private async Task<(LibraryEntityKind Kind, string Id)?> FindAsync(IReadOnlyList<LibraryEntityKind> kinds, string value, CancellationToken cancellationToken)
    {
        var isId = Guid.TryParse(value, out _);
        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case LibraryEntityKind.Agent when isId && (await agents.GetAsync(value, cancellationToken).ConfigureAwait(false)).IsSuccess:
                    return (kind, value);
                case LibraryEntityKind.Team when isId && (await teams.GetAsync(value, cancellationToken).ConfigureAwait(false)).IsSuccess:
                    return (kind, value);
                case LibraryEntityKind.Workflow:
                    if (isId && (await workflows.GetAsync(value, cancellationToken).ConfigureAwait(false)).IsSuccess)
                    {
                        return (kind, value);
                    }

                    // A nested workflow may be called by its name.
                    var byName = (await workflows.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).FirstOrDefault(w => string.Equals(w.Name, value, StringComparison.OrdinalIgnoreCase));
                    if (byName is not null)
                    {
                        return (kind, byName.Id);
                    }

                    break;
            }
        }

        return null;
    }

    // ── What the user must have ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> FamiliesOf(Collected collected, IReadOnlyList<Agentivity.Domain.Credentials.Credentials> all)
    {
        var families = new SortedSet<string>(StringComparer.Ordinal);
        if (collected.HasModelNodes)
        {
            families.Add("llm");
        }

        foreach (var credential in all.Where(c => collected.CredentialIds.Contains(c.Id)))
        {
            families.Add(Agentivity.Infrastructure.Core.Llm.LlmCredentialTester.IsLanguageModelTemplate(credential.TemplateId) ? Agentivity.Infrastructure.Core.Llm.LlmCredentialTester.LanguageModelFamily : credential.TemplateId.ToLowerInvariant());
        }

        return [.. families];
    }

    // ── The author's values ───────────────────────────────────────────────────────────────────

    /// <summary>The ten languages a Template can be written in (ISO 639-1): the most spoken, as the Studio and the marketplace offer them.</summary>
    public static readonly IReadOnlyList<string> SupportedLanguages = ["en", "zh", "hi", "es", "fr", "ar", "pt", "ru", "de", "ja"];

    private sealed record Values(string Id, string Version, string Name, string? Tagline, string Description, IReadOnlyList<string> Tags, string Language, TemplateCoverInput? Cover, TemplateAuthor? Author, string? License);

    private static TemplateSuggestion Suggest(TemplateExportRequest request, Item root, string? goal)
    {
        var name = FirstText(request.Name, root.Name) ?? string.Empty;
        var description = FirstText(request.Description, root.Role, goal) ?? string.Empty;
        var tags = NormalizeTags(request.Tags ?? root.Tags).Where(t => t != SolutionTag).Take(MaxTags).ToList();
        return new TemplateSuggestion(FirstText(request.Id) ?? ToSlug(name), name, description, tags);
    }

    private static Values Validate(TemplateExportRequest request, TemplateSuggestion suggestion, List<TemplateProblem> problems)
    {
        void Problem(string field, string code, string message) => problems.Add(new TemplateProblem(field, code, message));

        var id = suggestion.Id;
        if (id.Length is < 3 or > 64 || !Slug().IsMatch(id))
        {
            Problem("id", "invalid_id", "The identifier is 3 to 64 characters: lower-case letters, digits and hyphens (for example 'seo-content-factory').");
        }

        var version = FirstText(request.Version) ?? "1.0.0";
        if (!SemVer().IsMatch(version))
        {
            Problem("version", "invalid_version", "The version looks like 1.0.0.");
        }

        var name = suggestion.Name;
        if (name.Length is < 3 or > 80)
        {
            Problem("name", "invalid_name", "The name is 3 to 80 characters.");
        }

        var tagline = FirstText(request.Tagline);
        if (tagline is { Length: > 120 })
        {
            Problem("tagline", "tagline_too_long", "The tagline is 120 characters at most.");
        }

        var description = suggestion.Description;
        if (description.Length is < 20 or > 2000)
        {
            Problem("description", "invalid_description", "Describe what it does and for whom, in 20 to 2000 characters.");
        }

        var tags = suggestion.Tags;
        if ((request.Tags ?? []).Count(t => !string.IsNullOrWhiteSpace(t)) > MaxTags + 1)
        {
            Problem("tags", "too_many_tags", $"{MaxTags} tags at most.");
        }

        var language = (FirstText(request.Language) ?? "en").ToLowerInvariant();
        if (!SupportedLanguages.Contains(language))
        {
            Problem("language", "invalid_language", $"The language is one of: {string.Join(", ", SupportedLanguages)}.");
        }

        var author = request.Author;
        if (author is null || FirstText(author.Name) is not { Length: >= 2 and <= 80 })
        {
            Problem("author", "author_required", "Give the author's name (2 to 80 characters).");
        }
        else if (author.Url is { Length: > 0 } url && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Problem("author", "invalid_author_url", "The author's address starts with https://.");
        }

        var cover = request.Cover;
        if (cover is { DataUri.Length: > 0 })
        {
            var match = ImageDataUri().Match(cover.DataUri);
            if (!match.Success)
            {
                Problem("cover", "invalid_cover", "The cover is an embedded png, jpeg, webp or svg picture (a data: address).");
            }
            else if (DecodedLength(match.Groups["data"].Value) is not { } length || length > MaxCoverBytes)
            {
                Problem("cover", "cover_too_large", $"The cover is {MaxCoverBytes / 1024} KB at most.");
            }
        }
        else
        {
            cover = null;
        }

        var license = FirstText(request.License);
        return new Values(id, version, name, tagline, description, tags, language, cover, author, license);
    }

    private static int? DecodedLength(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64).Length;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? FirstText(params string?[] values) => values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        tags.Select(t => NotTag().Replace(NotSlug().Replace(t.Trim().ToLowerInvariant(), "-"), string.Empty).Trim('-'))
            .Where(t => t.Length is >= 2 and <= 32)
            .Distinct()
            .ToList();

    private static string ToSlug(string text)
    {
        var slug = NotSlug().Replace(text.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length > 64 ? slug[..64].Trim('-') : slug;
    }

    // ── The file ──────────────────────────────────────────────────────────────────────────────

    private static JsonObject BuildFile(TemplateExportRequest request, Values v, Collected collected, IReadOnlyList<string> families, TemplateRoot root)
    {
        var file = new JsonObject
        {
            ["schema"] = "agentivity.template",
            ["schemaVersion"] = 1,
            ["id"] = v.Id,
            ["version"] = v.Version,
            ["name"] = v.Name,
        };
        if (v.Tagline is not null)
        {
            file["tagline"] = v.Tagline;
        }

        file["description"] = v.Description;
        file["tags"] = new JsonArray(v.Tags.Select(t => (JsonNode?)t).ToArray());
        file["language"] = v.Language;
        if (v.Cover is { DataUri: { Length: > 0 } dataUri })
        {
            var cover = new JsonObject { ["dataUri"] = dataUri };
            if (FirstText(v.Cover.Alt) is { } alt)
            {
                cover["alt"] = alt;
            }

            file["cover"] = cover;
        }

        var author = new JsonObject { ["name"] = v.Author?.Name.Trim() };
        if (FirstText(v.Author?.Url) is { } url)
        {
            author["url"] = url;
        }

        if (FirstText(v.Author?.Id) is { } authorId)
        {
            author["id"] = authorId;
        }

        file["author"] = author;
        if (v.License is not null)
        {
            file["license"] = v.License;
        }

        file["createdAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        file["root"] = new JsonObject { ["kind"] = root.Kind.ToString().ToLowerInvariant(), ["id"] = collected.Items[0].Id };
        file["requires"] = new JsonObject { ["credentials"] = new JsonArray(families.Select(f => (JsonNode?)f).ToArray()), ["plugins"] = new JsonArray() };

        var entities = new JsonArray();
        foreach (var item in collected.Items)
        {
            var entry = item.Entry.DeepClone();
            if (item == collected.Items[0] && entry is JsonObject rootEntry)
            {
                TagRoot(rootEntry);
            }

            var entity = new JsonObject { ["kind"] = item.Kind, ["id"] = item.Id, ["name"] = item.Name, ["entry"] = entry };
            if (item.ToConfigure is { Count: > 0 } settings)
            {
                // Left out because they are the author's own: the person fills them in after the import.
                entity["toConfigure"] = new JsonArray(settings.Select(s => (JsonNode?)s).ToArray());
            }

            entities.Add(entity);
        }

        file["entities"] = entities;

        if (request.IncludeRows && collected.Rows.Count > 0)
        {
            var data = new JsonObject();
            foreach (var (tableId, rows) in collected.Rows)
            {
                data[tableId] = new JsonObject { ["rows"] = rows.DeepClone() };
            }

            file["data"] = data;
        }

        return file;
    }

    /// <summary>The root carries the tag "solution", so that an imported Template shows up as a Solution in the user's library.</summary>
    private static void TagRoot(JsonObject entry)
    {
        var tags = entry["tags"] as JsonArray ?? [];
        if (!tags.Any(t => string.Equals(t?.GetValue<string>(), SolutionTag, StringComparison.OrdinalIgnoreCase)))
        {
            tags.Add(SolutionTag);
        }

        entry["tags"] = tags;
    }
}
