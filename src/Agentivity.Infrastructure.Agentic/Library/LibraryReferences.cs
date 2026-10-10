using System.Text.Json;
using Agentivity.Infrastructure.Workflows;

namespace Agentivity.Infrastructure.Agentic.Library;

/// <summary>A pointer from one item of the library to another: the kinds it may designate and what the node says (an id, or a name).</summary>
internal sealed record LibraryReference(IReadOnlyList<LibraryEntityKind> Kinds, string Value);

/// <summary>Where the nodes of a graph say which other item of the library they call, or which data table they use.</summary>
internal static class LibraryReferences
{
    /// <summary>The nodes that call another item of the library, and where they say which one.</summary>
    public static void Collect(WorkflowNodeDefinition node, List<LibraryReference> references)
    {
        string? Text(string input) => node.GetParameter(input) is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

        void Add(string? value, params LibraryEntityKind[] kinds)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                references.Add(new LibraryReference(kinds, value));
            }
        }

        switch (node.NodeType.ToLowerInvariant())
        {
            case "agentic_node":
                Add(Text("agent_id"), LibraryEntityKind.Agent, LibraryEntityKind.Team);
                break;
            case "team_node":
                Add(Text("entity_id"), LibraryEntityKind.Team);
                break;
            case "composable_node":
                var kinds = Text("entity_kind")?.ToLowerInvariant() switch
                {
                    "agent" => new[] { LibraryEntityKind.Agent },
                    "team" => new[] { LibraryEntityKind.Team },
                    "workflow" => new[] { LibraryEntityKind.Workflow },
                    _ => new[] { LibraryEntityKind.Agent, LibraryEntityKind.Team, LibraryEntityKind.Workflow },
                };
                Add(Text("entity_id"), kinds);
                break;
            case "workflow.run":
                Add(Text("workflow"), LibraryEntityKind.Workflow);
                break;
        }
    }

    /// <summary>The shared data table a node is bound to (the data table nodes name it in <c>table_id</c>), if any.</summary>
    public static string? DataTableId(WorkflowNodeDefinition node) => node.GetParameter("table_id") is { ValueKind: JsonValueKind.String } element && !string.IsNullOrWhiteSpace(element.GetString()) ? element.GetString()!.Trim() : null;
}
