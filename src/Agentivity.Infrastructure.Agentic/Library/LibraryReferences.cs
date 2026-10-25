using System.Text.Json;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

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

        if (LibraryCallSteps.KindOf(node.NodeType) is { } kind)
        {
            Add(Text(LibraryCallSteps.EntityInput), LibraryKinds.Of(kind));
        }
    }

    /// <summary>The shared data table a node is bound to (the data table nodes name it in <c>table_id</c>), if any.</summary>
    /// <summary>The Knowledge Base a step searches (the Knowledge Base step), or null.</summary>
    public static string? KnowledgeBaseId(WorkflowNodeDefinition node) => node.GetParameter("knowledge_base_id") is { ValueKind: JsonValueKind.String } element && !string.IsNullOrWhiteSpace(element.GetString()) ? element.GetString()!.Trim() : null;

    public static string? DataTableId(WorkflowNodeDefinition node) => node.GetParameter("table_id") is { ValueKind: JsonValueKind.String } element && !string.IsNullOrWhiteSpace(element.GetString()) ? element.GetString()!.Trim() : null;
}
