using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Registry;

namespace Agentivity.Infrastructure.Workflows;

public sealed class WorkflowExecutionException : Exception
{
    public WorkflowExecutionException(string workflowId, Guid runId, string? nodeId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        WorkflowId = workflowId;
        RunId = runId;
        NodeId = nodeId;
    }

    public string WorkflowId { get; }

    public Guid RunId { get; }

    public string? NodeId { get; }
}

/// <summary>
/// Captures the full execution context of a workflow suspended by an interaction node,
/// allowing the DAG traversal to resume exactly where it left off.
/// </summary>
public sealed record SuspendedRunContext(
    Guid RunId,
    WorkflowEntity Definition,
    string SuspendedNodeId,
    Guid InteractionRequestId,
    Dictionary<string, object?> State,
    List<string> ExecutedNodes,
    HashSet<string> VisitedNodes,
    Queue<string> Pending,
    Dictionary<string, int> RemainingPredecessors,
    RunControl RunControl
);

public enum WorkflowNodeRunState
{
    NotStarted,
    Running,
    Completed,
    Failed,

    /// <summary>The step asked a person something and waits for the answer.</summary>
    WaitingForInput,
}

public sealed record WorkflowNodeRunStatus(string NodeId, WorkflowNodeRunState State, string? Error, IReadOnlyDictionary<string, object?>? Inputs = null, IReadOnlyDictionary<string, object?>? Outputs = null, IReadOnlyList<string>? StatusMessages = null, DebugTraceItem? Trace = null);

public sealed record WorkflowConnectionRunStatus(string From, string FromPort, string To, string ToPort, bool Traversed, string? Error);
