using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Display;

/// <summary>
/// Shared plumbing for "render this value directly on the node" nodes (the Console, the Image
/// Viewer, and future siblings) — pass-through action nodes: whatever comes in on <c>value</c>
/// goes straight back out unchanged, so they can sit anywhere in a graph without altering data.
/// <para>
/// Self-registers with <see cref="DisplayNodeRegistry"/> so <see cref="ResourceNodeRegistry.GetNodeClass"/>
/// reports <see cref="NodeClass.Display"/> for these node types — the Studio uses that to pick a
/// resizable, custom-rendered node wrapper instead of the standard icon/name tile. The node type
/// is the single source of truth for "this renders specially"; nothing about that is decided
/// client-side.
/// </para>
/// <para>
/// Deliberately thin: each subclass owns its own <c>value</c> input/output pair (they're typed
/// differently — plain text for the Console, an image reference for the Image Viewer) and just
/// tells this base how to pull the resolved value out of its config.
/// </para>
/// </summary>
public abstract class DisplayNode : ToolNode
{
    protected DisplayNode(string name, string displayName, string description, string category, string whenToUse) : base(name)
    {
        DisplayNodeRegistry.Register(name);

        DefineMetadata(displayName, description, category, whenToUse: whenToUse);
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");
    }

    /// <summary>Reads this node's own value-typed input (declared by the subclass) off the resolved config.</summary>
    protected abstract object? ResolveValue(NodeConfig config);

    /// <summary>Writes the resolved value to the <c>value</c> output (declared by the subclass) in its native shape.</summary>
    protected abstract void WriteValueOutput(NodeResult result, object? value);

    public sealed override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        var result = NodeResult.Create();
        WriteValueOutput(result, ResolveValue(config));
        return ValueTask.FromResult(result);
    }
}
