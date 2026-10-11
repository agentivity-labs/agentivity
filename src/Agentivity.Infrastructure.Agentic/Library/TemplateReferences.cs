using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Agentic.Library;

/// <summary>One place of a stored node (as JSON) that points to another entity of the Template.</summary>
/// <param name="Inputs">The node's <c>inputs</c> object, where the value is written.</param>
/// <param name="Key">The input that holds the reference.</param>
/// <param name="Kinds">What it may designate: an agent, a team, a workflow.</param>
/// <param name="IsDataTable">True when it points to a shared data table rather than to an agent, a team or a workflow.</param>
public readonly record struct NodeReference(JsonObject Inputs, string Key, string Value, IReadOnlyList<LibraryEntityKind> Kinds, bool IsDataTable);

/// <summary>
/// The same references as <see cref="LibraryReferences"/>, read from the stored JSON of an entity: the import rewrites them to the
/// new identifiers before the entity is ever turned back into a typed object.
/// </summary>
public static class TemplateReferences
{
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
        switch (nodeType)
        {
            case "agentic_node":
                if (Text("agent_id") is { } agent)
                {
                    yield return new NodeReference(inputs, "agent_id", agent, [LibraryEntityKind.Agent, LibraryEntityKind.Team], false);
                }

                break;
            case "team_node":
                if (Text("entity_id") is { } team)
                {
                    yield return new NodeReference(inputs, "entity_id", team, [LibraryEntityKind.Team], false);
                }

                break;
            case "composable_node":
                if (Text("entity_id") is { } entity)
                {
                    IReadOnlyList<LibraryEntityKind> kinds = Text("entity_kind")?.ToLowerInvariant() switch
                    {
                        "agent" => [LibraryEntityKind.Agent],
                        "team" => [LibraryEntityKind.Team],
                        "workflow" => [LibraryEntityKind.Workflow],
                        _ => [LibraryEntityKind.Agent, LibraryEntityKind.Team, LibraryEntityKind.Workflow],
                    };
                    yield return new NodeReference(inputs, "entity_id", entity, kinds, false);
                }

                break;
            case "workflow.run":
                if (Text("workflow") is { } workflow)
                {
                    yield return new NodeReference(inputs, "workflow", workflow, [LibraryEntityKind.Workflow], false);
                }

                break;
        }

        // The data table nodes name their table in table_id.
        if (Text("table_id") is { } table)
        {
            yield return new NodeReference(inputs, "table_id", table, [], true);
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
