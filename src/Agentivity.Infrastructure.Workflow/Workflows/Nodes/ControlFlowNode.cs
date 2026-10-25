using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Workflows.ControlFlow;

namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// Base class for control flow nodes (foreach, if, switch, while, etc.).
/// Control flow nodes are handled specially by the WorkflowEngine.
/// </summary>
public abstract class ControlFlowNode : BaseNode, IControlFlowNode
{
    protected ControlFlowNode(string name)
        : base(name) { }

    /// <summary>
    /// Evaluates the control flow logic.
    /// </summary>
    public abstract ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default);

    /// <summary>
    /// ExecuteAsync is not used for control flow nodes.
    /// The engine calls EvaluateAsync instead.
    /// </summary>
    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        // This should not be called directly for control flow nodes
        throw new InvalidOperationException($"Control flow node '{Name}' should be executed via EvaluateAsync, not ExecuteAsync. " + "Ensure the WorkflowEngine properly handles IControlFlowNode.");
    }
}
