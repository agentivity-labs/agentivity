using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Execution;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Runtime services bundle for a single node execution.
/// Contains delegates and infrastructure references needed to interact with the workflow engine
/// (checkpointing, status messages, trace updates, telemetry) — with no graph/config data.
/// Implements <see cref="IRunService"/> — the universal runtime services contract.
/// </summary>
public sealed class WorkflowRunServices : IAgentRunServices
{
    public WorkflowRunServices(
        Guid runId,
        Func<CancellationToken, ValueTask> checkpointAsync,
        Action<string> publishStatus,
        Action<Action<DebugTraceAccumulator>> updateTrace,
        IAgentTelemetryEmitter? telemetryEmitter = null,
        ExecutionCorrelationContext? correlationContext = null,
        ILogger? logger = null,
        string? executionId = null,
        string? streamId = null,
        int? callNumber = null
    )
    {
        RunId = runId;
        CheckpointAsync = checkpointAsync ?? throw new ArgumentNullException(nameof(checkpointAsync));
        PublishStatus = publishStatus ?? throw new ArgumentNullException(nameof(publishStatus));
        UpdateTrace = updateTrace ?? throw new ArgumentNullException(nameof(updateTrace));
        TelemetryEmitter = telemetryEmitter;
        CorrelationContext = correlationContext;
        Logger = logger;
        ExecutionId = executionId;
        StreamId = streamId;
        CallNumber = callNumber;
    }

    /// <summary>Unique identifier of the current workflow run.</summary>
    public Guid RunId { get; }

    /// <summary>
    /// The execution chain's stable ExecutionId (shared by all runs in a standalone/team/workflow
    /// chain). Threaded through so <c>InteractionRequest</c>s built for HIL dispatch carry it —
    /// without it, the frontend's StreamId-keyed event subscription never sees the request.
    /// </summary>
    public string? ExecutionId { get; }

    /// <summary>The execution's SSE channel identifier. See <see cref="ExecutionId"/>.</summary>
    public string? StreamId { get; }

    /// <summary>
    /// Which call of this node within the run this is, when the run calls the same node more than once —
    /// today the iterations of a loop body (1, 2, 3…). <see langword="null"/> for a node that runs once,
    /// which is every node outside a loop. What a call keeps apart from the others (an agent's session)
    /// is keyed on it: a suspended call and its resume are the same call, hence the same number.
    /// </summary>
    public int? CallNumber { get; }

    /// <summary>
    /// Checkpoints the current run — allows pause/resume/cancel operations.
    /// No-op when not running under a workflow engine (e.g. unit tests).
    /// </summary>
    public Func<CancellationToken, ValueTask> CheckpointAsync { get; }

    /// <summary>
    /// Publishes a status message visible in the workflow UI node display.
    /// Fire-and-forget — never throws.
    /// </summary>
    public Action<string> PublishStatus { get; }

    /// <summary>
    /// Updates the debug trace accumulator for the current node.
    /// Fire-and-forget — never throws.
    /// </summary>
    public Action<Action<DebugTraceAccumulator>> UpdateTrace { get; }

    /// <summary>OTel telemetry emitter. Null when telemetry is not configured.</summary>
    public IAgentTelemetryEmitter? TelemetryEmitter { get; }

    /// <summary>OTel correlation context for this node execution.</summary>
    public ExecutionCorrelationContext? CorrelationContext { get; }

    /// <summary>Logger for internal diagnostics. Null in test contexts.</summary>
    public ILogger? Logger { get; }

    /// <summary>
    /// Emits a debug-level log message. No-op when logger is null or below Debug threshold.
    /// </summary>
    public void LogDebug(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            Logger?.LogDebug("{NodeMessage}", message);
    }

    // ── IRunService explicit members ──────────────────────────────────────────
    // Properties are already exposed with the same names; the interface methods
    // delegate to the stored delegates so callers holding IRunService still work.

    ValueTask IRunService.CheckpointAsync(CancellationToken cancellationToken) => CheckpointAsync(cancellationToken);

    void IRunService.PublishStatus(string message) => PublishStatus(message);

    void IRunService.UpdateTrace(Action<DebugTraceAccumulator> updater) => UpdateTrace(updater);

    void IRunService.LogDebug(string message) => LogDebug(message);

    Guid IRunService.RunId => RunId;

    ExecutionCorrelationContext? IRunService.CorrelationContext => CorrelationContext;

    /// <summary>
    /// A no-op <see cref="WorkflowRunServices"/> instance for use in tests or contexts
    /// where no runtime services are available.
    /// </summary>
    public static readonly WorkflowRunServices Null = new(runId: Guid.Empty, checkpointAsync: _ => ValueTask.CompletedTask, publishStatus: _ => { }, updateTrace: _ => { });
}
