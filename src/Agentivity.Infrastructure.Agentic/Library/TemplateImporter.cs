using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Core.Json;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Templates;
using Agentivity.Infrastructure.Workflows;

namespace Agentivity.Infrastructure.Agentic.Library;

/// <summary>
/// Brings a Template into the user's library: everything it carries is created as new items (fresh identifiers, every reference
/// between them rewritten), the shared data tables come with their rows, and the model connections of the author are pointed at
/// one of the user's own. Nothing that was already there is modified. A first call with <c>dryRun</c> says what would be created;
/// an import that fails half-way removes what it had created. The format is described in
/// <c>delivery/features/templates/template-format.md</c>.
/// </summary>
public sealed partial class TemplateImporter(
    IEntityRepository<AgentEntity> agents,
    IEntityRepository<TeamEntity> teams,
    IEntityRepository<WorkflowEntity> workflows,
    LlmConnectionReplacer replacer,
    ITemplateDataTableSink? dataTables = null
)
{
    private const string ModelNodePrefix = "ai.llm.model.";
    private static readonly string[] Kinds = ["agent", "team", "workflow", "datatable"];

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVer();

    private sealed class Item(string kind, string oldId, string name, JsonObject json)
    {
        public string Kind { get; } = kind;
        public string OldId { get; } = oldId;
        public string NewId { get; } = Guid.NewGuid().ToString();
        public string Name { get; } = name;
        public JsonObject Json { get; } = json;
    }

    private readonly record struct Resolution(string State, Item? Target, bool ByName);

    public async Task<TemplateImportResult> ImportAsync(JsonObject file, TemplateImportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<TemplateProblem>();
        var warnings = new List<string>();
        var items = Parse(file, problems);
        var templateId = Text(file, "id") ?? string.Empty;
        var name = Text(file, "name") ?? templateId;
        var version = Text(file, "version") ?? string.Empty;

        var byId = items.ToDictionary(i => i.OldId, StringComparer.OrdinalIgnoreCase);
        var rootItem = CheckRoot(file, byId, problems);
        CheckReferences(items, byId, problems);
        CheckSafety(file, problems);

        var requires = ReadFamilies(file);
        var toConfigure = requires.Where(f => !string.Equals(f, "llm", StringComparison.OrdinalIgnoreCase)).ToList();
        var nodesFound = items.Sum(i => TemplateReferences.NodesOf(i.Kind, i.Json).Count(n => ((n["nodeType"] as JsonValue)?.ToString() ?? string.Empty).StartsWith(ModelNodePrefix, StringComparison.OrdinalIgnoreCase)));

        if (problems.Count > 0 || rootItem is null)
        {
            return new TemplateImportResult(options.DryRun, templateId, name, version, null, [], new TemplateImportModel("nothing_to_adapt", null, nodesFound, 0, null), requires, toConfigure, problems, warnings);
        }

        if (items.Any(i => i.Kind == "datatable") && dataTables is null)
        {
            problems.Add(new TemplateProblem("entities", "data_tables_unavailable", "This Template carries data tables, and the Data Tables module is not installed here."));
            return new TemplateImportResult(options.DryRun, templateId, name, version, null, [], new TemplateImportModel("nothing_to_adapt", null, nodesFound, 0, null), requires, toConfigure, problems, warnings);
        }

        // The model connection is chosen before anything is created: an unknown one stops the import early.
        var credential = await ResolveModelAsync(options.ModelConnection, cancellationToken).ConfigureAwait(false);

        var taken = (await workflows.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in items.Where(i => i.Kind == "workflow" && taken.Contains(i.Name)))
        {
            warnings.Add($"A workflow named '{workflow.Name}' already exists: nodes that call a workflow by its name may reach either of them.");
        }

        Remap(items, byId);
        var root = new TemplateRoot(ToLibraryKind(rootItem.Kind), rootItem.NewId);

        if (options.DryRun)
        {
            var preview = items.Select(i => new TemplateImportItem(i.Kind, i.OldId, i.NewId, i.Name, "would_create")).ToList();
            return new TemplateImportResult(true, templateId, name, version, root, preview, ModelPlan(options, credential, nodesFound), requires, toConfigure, problems, warnings);
        }

        var created = new List<Item>();
        try
        {
            foreach (var item in items.OrderBy(i => i.Kind == "datatable" ? 0 : 1))
            {
                await SaveAsync(item, file, options.IncludeRows, cancellationToken).ConfigureAwait(false);
                created.Add(item);
            }
        }
        catch
        {
            await RollBackAsync(created).ConfigureAwait(false);
            throw;
        }

        var model = await AdaptModelAsync(options, credential, root, items, nodesFound, cancellationToken).ConfigureAwait(false);
        var done = items.Select(i => new TemplateImportItem(i.Kind, i.OldId, i.NewId, i.Name, "created")).ToList();
        return new TemplateImportResult(false, templateId, name, version, root, done, model, requires, toConfigure, problems, warnings);
    }

    // ── Reading the file ──────────────────────────────────────────────────────────────────────

    private static string? Text(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static List<Item> Parse(JsonObject file, List<TemplateProblem> problems)
    {
        void Problem(string field, string code, string message) => problems.Add(new TemplateProblem(field, code, message));

        if (Text(file, "schema") != "agentivity.template")
        {
            Problem("schema", "not_a_template", "This file is not an Agentivity Template.");
            return [];
        }

        if (file["schemaVersion"] is not JsonValue sv || !sv.TryGetValue<int>(out var schemaVersion) || schemaVersion != 1)
        {
            Problem("schemaVersion", "unsupported_version", "This Template was made with another version of the format than this server reads (1).");
            return [];
        }

        if (Text(file, "id") is not { } id || id.Length is < 3 or > 64)
        {
            Problem("id", "invalid_id", "The Template has no valid identifier.");
        }

        if (Text(file, "version") is not { } version || !SemVer().IsMatch(version))
        {
            Problem("version", "invalid_version", "The Template has no valid version (like 1.0.0).");
        }

        if (Text(file, "name") is null)
        {
            Problem("name", "invalid_name", "The Template has no name.");
        }

        var items = new List<Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (file["entities"] is not JsonArray entities || entities.Count == 0)
        {
            Problem("entities", "no_entities", "The Template contains nothing.");
            return items;
        }

        foreach (var node in entities)
        {
            if (node is not JsonObject element || Text(element, "kind")?.ToLowerInvariant() is not { } kind || !Kinds.Contains(kind))
            {
                Problem("entities", "invalid_entity", "An item has no known kind (agent, team, workflow or datatable).");
                continue;
            }

            var entry = ReadEntry(element);
            if (entry is null)
            {
                Problem("entities", "invalid_entity", $"The {kind} '{Text(element, "name") ?? "?"}' has no readable content.");
                continue;
            }

            var itemId = Text(element, "id") ?? Text(entry, "id");
            if (itemId is null)
            {
                Problem("entities", "invalid_entity", $"The {kind} '{Text(element, "name") ?? Text(entry, "name") ?? "?"}' has no identifier.");
                continue;
            }

            if (!seen.Add(itemId))
            {
                Problem("entities", "duplicate_id", $"Two items share the identifier '{itemId}'.");
                continue;
            }

            items.Add(new Item(kind, itemId, Text(element, "name") ?? Text(entry, "name") ?? itemId, entry));
        }

        return items;
    }

    /// <summary>The entity as an object: <c>entry</c>, or the older catalog wrapper (<c>entryJson</c>, an object or a JSON string).</summary>
    private static JsonObject? ReadEntry(JsonObject element)
    {
        if (element["entry"] is JsonObject entry)
        {
            return entry.DeepClone().AsObject();
        }

        if (element["entryJson"] is JsonObject wrapped)
        {
            return wrapped.DeepClone().AsObject();
        }

        if (element["entryJson"] is JsonValue text && text.TryGetValue<string>(out var json))
        {
            try
            {
                return JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static Item? CheckRoot(JsonObject file, Dictionary<string, Item> byId, List<TemplateProblem> problems)
    {
        if (file["root"] is not JsonObject root || Text(root, "id") is not { } rootId || Text(root, "kind")?.ToLowerInvariant() is not { } rootKind)
        {
            problems.Add(new TemplateProblem("root", "root_missing", "The Template does not say which item is its root."));
            return null;
        }

        if (!byId.TryGetValue(rootId, out var item) || item.Kind != rootKind || item.Kind == "datatable")
        {
            problems.Add(new TemplateProblem("root", "root_not_found", "The root is not one of the Template's agents, teams or workflows."));
            return null;
        }

        return item;
    }

    private static IReadOnlyList<string> ReadFamilies(JsonObject file) =>
        ((file["requires"] as JsonObject)?["credentials"] as JsonArray)?.Select(n => (n as JsonValue)?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.ToLowerInvariant()).Distinct().ToList() ?? [];

    private static void CheckSafety(JsonObject file, List<TemplateProblem> problems)
    {
        var text = file.ToJsonString();
        if (text.Length > TemplateExporter.MaxFileBytes)
        {
            problems.Add(new TemplateProblem("file", "file_too_large", $"The Template is larger than {TemplateExporter.MaxFileBytes / 1024 / 1024} MB."));
        }

        foreach (var kind in TemplateSecretScanner.Scan(text))
        {
            problems.Add(new TemplateProblem("entities", "secret_found", $"{kind} found in the Template: it is not imported."));
        }
    }

    // ── References ────────────────────────────────────────────────────────────────────────────

    private static Resolution Resolve(string value, IReadOnlyList<LibraryEntityKind> kinds, bool isTable, Dictionary<string, Item> byId, List<Item> all)
    {
        if (TemplateReferences.IsComputed(value))
        {
            return new Resolution("computed", null, false);
        }

        if (byId.TryGetValue(value, out var target))
        {
            var allowed = isTable ? target.Kind == "datatable" : kinds.Contains(ToLibraryKind(target.Kind)) && target.Kind != "datatable";
            return new Resolution(allowed ? "found" : "wrong_kind", allowed ? target : null, false);
        }

        // A nested workflow may be called by its name.
        if (!isTable && kinds.Contains(LibraryEntityKind.Workflow))
        {
            var named = all.Where(i => i.Kind == "workflow" && string.Equals(i.Name, value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 0)
            {
                return new Resolution("found", named[0], true);
            }
        }

        return new Resolution("not_found", null, false);
    }

    private static void CheckReferences(List<Item> items, Dictionary<string, Item> byId, List<TemplateProblem> problems)
    {
        void Check(Item from, string value, IReadOnlyList<LibraryEntityKind> kinds, bool isTable)
        {
            var resolution = Resolve(value, kinds, isTable, byId, items);
            if (resolution.State is "not_found" or "wrong_kind")
            {
                problems.Add(new TemplateProblem("entities", "reference_not_found", $"The {from.Kind} '{from.Name}' points to '{value}', which is not in the Template: it would be incomplete."));
            }
        }

        foreach (var item in items)
        {
            foreach (var node in TemplateReferences.NodesOf(item.Kind, item.Json))
            {
                foreach (var reference in TemplateReferences.In(node))
                {
                    Check(item, reference.Value, reference.Kinds, reference.IsDataTable);
                }
            }

            if (item.Kind == "team")
            {
                foreach (var (_, value, kind) in TemplateReferences.MembersOf(item.Json))
                {
                    Check(item, value, [kind], false);
                }
            }
        }
    }

    /// <summary>Gives every item its new identifier and rewrites every reference between them accordingly.</summary>
    private static void Remap(List<Item> items, Dictionary<string, Item> byId)
    {
        foreach (var item in items)
        {
            item.Json["id"] = item.NewId;
            item.Json.Remove("createdAt");
            item.Json.Remove("updatedAt");
            if (item.Kind == "agent" && item.Json["graph"] is JsonObject graph)
            {
                graph["id"] = Guid.NewGuid().ToString();
                graph.Remove("updatedAt");
                graph.Remove("createdAt");
            }

            foreach (var node in TemplateReferences.NodesOf(item.Kind, item.Json))
            {
                foreach (var reference in TemplateReferences.In(node))
                {
                    var resolution = Resolve(reference.Value, reference.Kinds, reference.IsDataTable, byId, items);
                    // A reference by name keeps the name (names are kept); one by identifier follows the new identifier.
                    if (resolution is { State: "found", ByName: false, Target: { } target })
                    {
                        reference.Inputs[reference.Key] = target.NewId;
                    }
                }
            }

            if (item.Kind == "team")
            {
                foreach (var (member, value, kind) in TemplateReferences.MembersOf(item.Json))
                {
                    var resolution = Resolve(value, [kind], false, byId, items);
                    if (resolution is { State: "found", Target: { } target })
                    {
                        member["memberEntityId"] = target.NewId;
                    }
                }
            }
        }
    }

    // ── Creating ──────────────────────────────────────────────────────────────────────────────

    private async Task SaveAsync(Item item, JsonObject file, bool includeRows, CancellationToken cancellationToken)
    {
        var text = item.Json.ToJsonString();
        try
        {
            switch (item.Kind)
            {
                case "agent":
                {
                    var entity = JsonSerializer.Deserialize<AgentEntity>(text, JsonDefaults.Default) ?? throw new JsonException("empty");
                    Require((await agents.SaveAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false)).IsSuccess, item);
                    break;
                }
                case "team":
                {
                    var entity = JsonSerializer.Deserialize<TeamEntity>(text, JsonDefaults.Default) ?? throw new JsonException("empty");
                    Require((await teams.SaveAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false)).IsSuccess, item);
                    break;
                }
                case "workflow":
                {
                    var entity = JsonSerializer.Deserialize<WorkflowEntity>(text, JsonDefaults.Default) ?? throw new JsonException("empty");
                    Require((await workflows.SaveAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false)).IsSuccess, item);
                    break;
                }
                default:
                {
                    var rows = includeRows && (file["data"] as JsonObject)?[item.OldId] is JsonObject tableData && tableData["rows"] is JsonArray array ? array.Select(r => JsonDocument.Parse(r!.ToJsonString()).RootElement.Clone()).ToList() : [];
                    var table = new TemplateDataTable(item.NewId, item.Name, JsonDocument.Parse(text).RootElement.Clone(), rows);
                    if (await dataTables!.CreateAsync(table, cancellationToken).ConfigureAwait(false) is { } error)
                    {
                        throw new TemplateImportException("save_failed", error);
                    }

                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new TemplateImportException("invalid_entity", $"The {item.Kind} '{item.Name}' could not be read: {ex.Message}");
        }
    }

    private static void Require(bool saved, Item item)
    {
        if (!saved)
        {
            throw new TemplateImportException("save_failed", $"The {item.Kind} '{item.Name}' could not be saved.");
        }
    }

    private async Task RollBackAsync(List<Item> created)
    {
        foreach (var item in Enumerable.Reverse(created))
        {
            try
            {
                switch (item.Kind)
                {
                    case "agent":
                        await agents.DeleteAsync(item.NewId).ConfigureAwait(false);
                        break;
                    case "team":
                        await teams.DeleteAsync(item.NewId).ConfigureAwait(false);
                        break;
                    case "workflow":
                        await workflows.DeleteAsync(item.NewId).ConfigureAwait(false);
                        break;
                    default:
                        if (dataTables is not null)
                        {
                            await dataTables.DeleteAsync(item.NewId).ConfigureAwait(false);
                        }

                        break;
                }
            }
            catch (Exception)
            {
                // Best effort: what could not be removed stays, and the error that stopped the import is the one reported.
            }
        }
    }

    // ── The model connection ──────────────────────────────────────────────────────────────────

    private async Task<LlmReplacementCredential?> ResolveModelAsync(string? choice, CancellationToken cancellationToken)
    {
        if (string.Equals(choice, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var named = !string.IsNullOrWhiteSpace(choice) && !string.Equals(choice, "default", StringComparison.OrdinalIgnoreCase);
        try
        {
            return await replacer.ResolveCredentialAsync(named ? choice : null, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmReplacementException ex) when (!named && ex.Code == "no_llm_credential")
        {
            // No connection yet: the Template is still imported, and the result says to add one.
            return null;
        }
        catch (LlmReplacementException ex)
        {
            throw new TemplateImportException(ex.Code, ex.Message);
        }
    }

    private static TemplateImportModel ModelPlan(TemplateImportOptions options, LlmReplacementCredential? credential, int nodesFound)
    {
        if (nodesFound == 0)
        {
            return new TemplateImportModel("nothing_to_adapt", credential, 0, 0, null);
        }

        if (string.Equals(options.ModelConnection, "keep", StringComparison.OrdinalIgnoreCase))
        {
            return new TemplateImportModel("kept", null, nodesFound, 0, "The author's model connection is kept: it does not exist on this server, the first run will fail until you change it.");
        }

        return credential is null
            ? new TemplateImportModel("no_connection", null, nodesFound, 0, "You have no model connection yet. Add one in Settings, then use 'Use my default model' on the imported items.")
            : new TemplateImportModel("will_adapt", credential, nodesFound, 0, null);
    }

    private async Task<TemplateImportModel> AdaptModelAsync(TemplateImportOptions options, LlmReplacementCredential? credential, TemplateRoot root, List<Item> items, int nodesFound, CancellationToken cancellationToken)
    {
        var plan = ModelPlan(options, credential, nodesFound);
        if (plan.Status != "will_adapt")
        {
            return plan;
        }

        try
        {
            var scope = items.Select(i => i.NewId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var report = await replacer.ReplaceAsync([new LlmReplacementRoot(root.Kind, root.Id)], credential!.Id, dryRun: false, scope, cancellationToken).ConfigureAwait(false);
            var failed = report.Items.Count(i => i.Status == "failed");
            return report.NodesReplaced == 0
                ? new TemplateImportModel("already_using", credential, nodesFound, 0, null)
                : new TemplateImportModel("adapted", credential, nodesFound, report.NodesReplaced, failed > 0 ? $"{failed} item(s) could not be updated: use 'Use my default model' on them." : null);
        }
        catch (LlmReplacementException ex)
        {
            return new TemplateImportModel("no_connection", null, nodesFound, 0, ex.Message);
        }
    }

    private static LibraryEntityKind ToLibraryKind(string kind) =>
        kind switch
        {
            "team" => LibraryEntityKind.Team,
            "workflow" => LibraryEntityKind.Workflow,
            _ => LibraryEntityKind.Agent,
        };
}
