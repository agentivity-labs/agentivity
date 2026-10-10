using System.Text.Json;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Engine-owned progress payload for a workflow run (EPIC RunRecord-unifié, Story 5) — stored as
/// <see cref="RunRecord.Payload"/> under
/// <c>PayloadType == "workflow"</c>. Replaces <c>PersistedWaitingRun</c>/<c>IWaitingRunStore</c>.
/// WorkflowId (via EntityId) and lifecycle (State/CreatedAt/CompletedAt/Error) already live on the
/// generic <c>RunRecord</c>/<c>RunContext</c> — not duplicated here. Only written at HIL suspension
/// (a workflow's one checkpoint point); null/absent while running or once terminal.
/// </summary>
/// <param name="SuspendedNodeId">Id of the node that raised the HIL gate.</param>
/// <param name="InteractionRequestId">Pending interaction request awaiting a human response.</param>
/// <param name="State">Runtime state dict at suspension time (node outputs, DataTable snapshots, etc).</param>
/// <param name="ExecutedNodes">Nodes that have already run — replayed to the graph editor on resume.</param>
/// <param name="VisitedNodes">Nodes visited by the DAG walk (superset of ExecutedNodes for control-flow bookkeeping).</param>
/// <param name="Pending">Node ids queued for execution once the suspended node completes.</param>
/// <param name="RemainingPredecessors">Per-node count of predecessors not yet completed.</param>
/// <param name="SuspendedAt">When the suspension occurred.</param>
public sealed record WorkflowProgress(
    string SuspendedNodeId,
    Guid InteractionRequestId,
    Dictionary<string, JsonElement> State,
    List<string> ExecutedNodes,
    List<string> VisitedNodes,
    List<string> Pending,
    Dictionary<string, int> RemainingPredecessors,
    DateTime SuspendedAt
) : IRecordConvertible<JsonElement>
{
    public JsonElement ToRecord() => JsonSerializer.SerializeToElement(this);

    public static WorkflowProgress FromRecord(JsonElement record) =>
        JsonSerializer.Deserialize<WorkflowProgress>(record) ?? throw new InvalidOperationException("WorkflowProgress payload could not be deserialized.");
}
