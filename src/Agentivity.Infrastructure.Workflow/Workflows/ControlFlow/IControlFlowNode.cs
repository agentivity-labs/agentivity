namespace Agentivity.Infrastructure.Workflows.ControlFlow;

/// <summary>
/// Interface for nodes that control execution flow (loops, conditions, etc.).
/// These nodes are handled specially by the WorkflowEngine.
/// </summary>
public interface IControlFlowNode
{
    /// <summary>
    /// Evaluates the control flow logic and returns the result.
    /// Called by the engine to determine how to handle this node.
    /// </summary>
    /// <param name="config">The node execution config.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The control flow result indicating how the engine should proceed.</returns>
    ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default);
}
