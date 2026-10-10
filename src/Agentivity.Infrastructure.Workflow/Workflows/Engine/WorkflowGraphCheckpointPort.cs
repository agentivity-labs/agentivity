using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// <see cref="IGraphCheckpointPort"/> adapter that persists HIL suspension state as
/// <see cref="WorkflowProgress"/> in the run's own <see cref="RunRecord"/>.Payload (EPIC
/// RunRecord-unifié, Story 5) when GraphHost catches an <see cref="IGraphSignalException"/>.
/// Replaces the old <c>WorkflowExecutionStore</c>/<c>IWaitingRunStore</c> pair — no separate file
/// per suspended run anymore. Set <see cref="Orchestrator"/> after construction but before
/// graph submission.
/// </summary>
internal sealed class WorkflowGraphCheckpointPort : IGraphCheckpointPort
{
    private readonly IRepository<RunContext, RunRecord> _runRepository;

    internal WorkflowGraphOrchestrator? Orchestrator { get; set; }

    public WorkflowGraphCheckpointPort(IRepository<RunContext, RunRecord> runRepository)
    {
        _runRepository = runRepository ?? throw new ArgumentNullException(nameof(runRepository));
    }

    /// <inheritdoc/>
    public ValueTask SaveAsync(GraphId graphId, GraphRestorePoint restorePoint, CancellationToken cancellationToken = default)
    {
        var orch = Orchestrator;
        if (orch is null)
            return ValueTask.CompletedTask;

        var hil = orch.WorkflowContext.HilSuspension;
        if (hil is null)
            return ValueTask.CompletedTask; // Only persist for HIL suspensions; regular task completions are no-ops here.

        var progress = new WorkflowProgress(
            SuspendedNodeId: hil.NodeId,
            InteractionRequestId: hil.InteractionRequestId,
            State: WorkflowStateSerializer.SerializeState(orch.WorkflowContext.State),
            ExecutedNodes: [.. orch.WorkflowContext.ExecutedNodes],
            VisitedNodes: [.. orch.WorkflowContext.VisitedNodes],
            Pending: orch.GetPendingNodeIds(),
            RemainingPredecessors: new Dictionary<string, int>(orch.GetRemainingPredecessors(), StringComparer.OrdinalIgnoreCase),
            SuspendedAt: DateTime.UtcNow
        );

        SetPayload(orch.RunId.ToString("D"), "workflow", progress.ToRecord());
        return ValueTask.CompletedTask;
    }

    // ── Sync bridging helper over the async IRepository surface ─────────────
    // FindAsync's cache-hit path is synchronous under the hood (ConcurrentDictionary
    // lookup, no I/O) — same reasoning applied elsewhere in this codebase.
    private void SetPayload(string runId, string payloadType, System.Text.Json.JsonElement payload)
    {
        var ctx = _runRepository.FindAsync(runId).AsTask().GetAwaiter().GetResult();
        if (ctx is null) return;
        ctx.SetPayload(payloadType, payload);
        _runRepository.AddAsync(ctx).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Resume is handled by <see cref="WorkflowEngine.ProvideInteractionResponseAsync"/>
    /// which loads the persisted progress directly via <see cref="IRepository{RunContext, RunRecord}"/>.
    /// </summary>
    public ValueTask<GraphRestorePoint?> LoadAsync(GraphId graphId, CancellationToken cancellationToken = default) => ValueTask.FromResult<GraphRestorePoint?>(null);

    /// <inheritdoc/>
    public ValueTask DeleteAsync(GraphId graphId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
