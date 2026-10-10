using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflows.Nodes;

public abstract class ToolNode : BaseNode
{
    protected ToolNode(string name)
        : base(name) { }

    /// <summary>
    /// Executes the node and returns a strongly-typed NodeResult.
    /// </summary>
    public abstract override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default);
}

/// <summary>
/// Base class for resource nodes (LLM models, storage, scrapers, content tools, etc.).
/// Resource nodes are never executed by the workflow engine — they provide configuration
/// and capabilities to consumer nodes via resource ports.
/// Consumers reach them through <see cref="NodeInstanceRegistry"/>, which asks the node factory for a new instance
/// each time — a resource node is never kept, so it must not keep anything either.
/// </summary>
public abstract class ResourceNode : BaseNode
{
    protected ResourceNode(string name)
        : base(name)
    {
        NodeInstanceRegistry.NoteNodeCreated(name, this); // a no-op in the application — see RegistersNodesWhenCreated
    }

    /// <summary>
    /// Resource nodes are not executed by the workflow engine.
    /// Their configuration is read directly from the workflow definition via ResourceBinding.
    /// </summary>
    public sealed override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException($"Resource node '{Name}' cannot be executed directly. Use ResourceResolver to access its configuration.");
    }
}
