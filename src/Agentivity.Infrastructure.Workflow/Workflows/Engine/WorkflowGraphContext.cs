using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Workflows.Interactions;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Records the HIL suspension details for the current graph execution,
/// populated by <see cref="WorkflowNodeTask"/> when a <see cref="WorkflowSuspendedException"/> is re-thrown.
/// Consumed by <see cref="WorkflowExecutionStore"/> and <see cref="WorkflowExecutionLoop"/> after the graph signals.
/// </summary>
internal sealed record HilSuspensionRecord(string NodeId, Guid InteractionRequestId, IReadOnlyDictionary<string, object?>? PartialOutputs);

/// <summary>
/// Carries the shared mutable state for a single workflow execution across all its nodes.
/// Passed to every <see cref="WorkflowNodeTask"/> via <see cref="GraphTask.Context{T}"/>.
/// </summary>
internal sealed class WorkflowGraphContext : GraphExecutionContext
{
    public Dictionary<string, object?> State { get; }
    public List<string> ExecutedNodes { get; } = [];
    public HashSet<string> VisitedNodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set when a node throws <see cref="WorkflowSuspendedException"/> and re-throws it
    /// as an <see cref="IGraphSignalException"/>. Null during normal execution.
    /// </summary>
    internal HilSuspensionRecord? HilSuspension { get; private set; }

    public WorkflowGraphContext(Dictionary<string, object?> state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>
    /// Records the HIL suspension so <see cref="WorkflowExecutionStore"/> can persist it
    /// when the graph host calls <c>SaveAsync</c> after catching the signal exception.
    /// </summary>
    internal void RecordHilSuspension(string nodeId, WorkflowSuspendedException ex)
    {
        HilSuspension = new HilSuspensionRecord(nodeId, ex.Request.Id, ex.PartialOutputs);
    }
}
