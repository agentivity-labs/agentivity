using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Agentic.Library;

/// <summary>One place of a stored node (as JSON) that points to another entity of the Template.</summary>
/// <param name="Inputs">The node's <c>inputs</c> object, where the value is written.</param>
/// <param name="Key">The input that holds the reference.</param>
/// <param name="Kinds">What it may designate: an agent, a team, a workflow.</param>
/// <param name="AssetKind">The kind of item of data it points to (<c>datatable</c>, <c>knowledgebase</c>), or null when it points to an agent, a team or a workflow.</param>
public readonly record struct NodeReference(JsonObject Inputs, string Key, string Value, IReadOnlyList<LibraryEntityKind> Kinds, string? AssetKind)
{
    /// <summary>True when it points to a shared data table.</summary>
    public bool IsDataTable => AssetKind == TemplateReferences.DataTableKind;
}

/// <summary>
/// The same references as <see cref="LibraryReferences"/>, read from the stored JSON of an entity: the import rewrites them to the
/// new identifiers before the entity is ever turned back into a typed object.
/// </summary>
public static class TemplateReferences
{
    /// <summary>The kinds of the items of data a Template carries beside its agents, teams and workflows.</summary>
    public const string DataTableKind = "datatable";

    public const string KnowledgeBaseKind = "knowledgebase";

    /// <summary>Whether an item of the Template is an item of data (a table, a base) rather than an agent, a team or a workflow.</summary>
    public static bool IsAsset(string kind) => kind is DataTableKind or KnowledgeBaseKind;

    /// <summary>The nodes of an agent (its graph) or of a workflow; a team has none.</summary>
    public static IEnumerable<JsonObject> NodesOf(string kind, JsonObject entry)
    {
        var nodes = kind switch
        {
            "agent" => (entry["graph"] as JsonObject)?["nodes"] as JsonArray,
            "workflow" => entry["nodes"] as JsonArray,
            _ => null,
        };
        return nodes?.OfType<JsonObject>() ?? [];
    }

    public static IEnumerable<NodeReference> In(JsonObject node)
    {
        if (node["inputs"] is not JsonObject inputs)
        {
            yield break;
        }

        string? Text(string key) => inputs[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

        var nodeType = (node["nodeType"] as JsonValue)?.GetValue<string>()?.ToLowerInvariant() ?? string.Empty;
        // The steps that call something of the library name it by its id, in the same input.
        if (Agentivity.Infrastructure.Workflows.Nodes.LibraryCallSteps.KindOf(nodeType) is { } kind && Text(Agentivity.Infrastructure.Workflows.Nodes.LibraryCallSteps.EntityInput) is { } called)
        {
            yield return new NodeReference(inputs, Agentivity.Infrastructure.Workflows.Nodes.LibraryCallSteps.EntityInput, called, [LibraryKinds.Of(kind)], null);
        }

        // The data table nodes name their table in table_id.
        if (Text("table_id") is { } table)
        {
            yield return new NodeReference(inputs, "table_id", table, [], DataTableKind);
        }

        // The Knowledge Base step names its base in knowledge_base_id.
        if (Text("knowledge_base_id") is { } knowledgeBase)
        {
            yield return new NodeReference(inputs, "knowledge_base_id", knowledgeBase, [], KnowledgeBaseKind);
        }
    }

    /// <summary>The members of a team: where each says which entity it is, and of what kind.</summary>
    public static IEnumerable<(JsonObject Member, string Value, LibraryEntityKind Kind)> MembersOf(JsonObject team)
    {
        foreach (var member in (team["members"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (member["memberEntityId"] is not JsonValue id || !id.TryGetValue<string>(out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var kind = ((member["memberType"] as JsonValue)?.ToString() ?? string.Empty).ToLowerInvariant() switch
            {
                "team" => LibraryEntityKind.Team,
                "workflow" => LibraryEntityKind.Workflow,
                _ => LibraryEntityKind.Agent,
            };
            yield return (member, value.Trim(), kind);
        }
    }

    public static bool IsComputed(string value) => value.Contains("{{", StringComparison.Ordinal) || value.StartsWith('$') || value.StartsWith('=');
}
