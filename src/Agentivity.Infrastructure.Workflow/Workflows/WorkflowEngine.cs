using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Agentivity.Domain.Run;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.AgUi;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Workflow.Services.WorkflowManagement;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Observability.Contracts.Events.Node;
using Agentivity.Observability.Contracts.Events.Run;
using Agentivity.Observability.Runtime;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Workflows;

public sealed partial class WorkflowEngine
{
    // ── Injected run registry ──────────────────────────────────────────────
    private readonly IRepository<RunContext, RunRecord> _runRepository;

    // Maps runId → workflowId so Notify can pass the correct DefinitionId to the event bus.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _runWorkflowIds = new(StringComparer.OrdinalIgnoreCase);

    private readonly WorkflowService _workflowService;
    private readonly INodeFactory _nodeFactory;
    private readonly ILogger<WorkflowEngine> _logger;
    private readonly IAgentTelemetryEmitter _telemetryEmitter;
    private readonly IGraphHost _graphHost;
    private readonly IRunEventBus? _eventBus;
    internal readonly ISystemResourceMonitor _resourceMonitor;
    private readonly IAgUiWidgetBundleRegistry? _agUiBundleRegistry;

    /// <summary>
    /// For tests: nodes built by hand are used exactly as given (see <see cref="NodeFactory.FromInstances"/>), and
    /// uses a private RunRepository instance. The application builds the engine from an <see cref="INodeFactory"/> instead.
    /// </summary>
    public WorkflowEngine(
        WorkflowService workflowService,
        IEnumerable<Node> nodes,
        ILogger<WorkflowEngine> logger,
        IGraphHost? graphHost = null,
        IAgentTelemetryEmitter? telemetryEmitter = null,
        IRunEventBus? eventBus = null,
        ISystemResourceMonitor? resourceMonitor = null,
        IAgUiWidgetBundleRegistry? agUiBundleRegistry = null
    )
        : this(workflowService, nodes, new RunRepository(), logger, graphHost, telemetryEmitter, eventBus, resourceMonitor, agUiBundleRegistry) { }

    /// <summary>For tests: nodes built by hand are used exactly as given (see <see cref="NodeFactory.FromInstances"/>).</summary>
    public WorkflowEngine(
        WorkflowService workflowService,
        IEnumerable<Node> nodes,
        IRepository<RunContext, RunRecord> runRepository,
        ILogger<WorkflowEngine> logger,
        IGraphHost? graphHost = null,
        IAgentTelemetryEmitter? telemetryEmitter = null,
        IRunEventBus? eventBus = null,
        ISystemResourceMonitor? resourceMonitor = null,
        IAgUiWidgetBundleRegistry? agUiBundleRegistry = null
    )
        : this(workflowService, InstallTestNodes(nodes), runRepository, logger, graphHost, telemetryEmitter, eventBus, resourceMonitor, agUiBundleRegistry) { }

    /// <summary>Tests hand the engine their nodes; code that resolves resource nodes through the registry must find those same objects.</summary>
    private static INodeFactory InstallTestNodes(IEnumerable<Node> nodes)
    {
        NodeFactory factory = NodeFactory.FromInstances(nodes ?? throw new ArgumentNullException(nameof(nodes)));
        NodeInstanceRegistry.Use(factory);
        return factory;
    }

    /// <summary>The application's constructor: every step runs on a node the factory builds for it.</summary>
    public WorkflowEngine(
        WorkflowService workflowService,
        INodeFactory nodeFactory,
        IRepository<RunContext, RunRecord> runRepository,
        ILogger<WorkflowEngine> logger,
        IGraphHost? graphHost = null,
        IAgentTelemetryEmitter? telemetryEmitter = null,
        IRunEventBus? eventBus = null,
        ISystemResourceMonitor? resourceMonitor = null,
        IAgUiWidgetBundleRegistry? agUiBundleRegistry = null
    )
    {
        _runRepository = runRepository ?? throw new ArgumentNullException(nameof(runRepository));
        _workflowService = workflowService ?? throw new ArgumentNullException(nameof(workflowService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetryEmitter = telemetryEmitter ?? new AgentTelemetryEmitter([]);
        _graphHost = graphHost ?? new GraphHost();
        _eventBus = eventBus;
        _resourceMonitor = resourceMonitor ?? NullSystemResourceMonitor.Instance;
        _agUiBundleRegistry = agUiBundleRegistry;

        _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
    }

    /// <summary>
    /// Exposes the injected run registry for callers that need to query run state
    /// (e.g. test helpers, admin endpoints).
    /// </summary>
    public IRepository<RunContext, RunRecord> Registry => _runRepository;

    // ── Sync bridging helpers over the async IRepository surface ────────────
    // FindAsync's cache-hit path (the overwhelming common case here) is synchronous under the
    // hood (ConcurrentDictionary lookup, no I/O) — same reasoning the codebase already applies to
    // blocking on the disk store's ValueTask surface. Shared across every WorkflowEngine partial.

    private RunContext? GetRun(string runId) => _runRepository.FindAsync(runId).AsTask().GetAwaiter().GetResult();

    private void SetRunState(string runId, RunStatus state, string? error = null, DateTimeOffset? completedAt = null)
    {
        var ctx = GetRun(runId);
        if (ctx is null) return;
        ctx.SetStatus(state, error, completedAt);
        _runRepository.AddAsync(ctx).AsTask().GetAwaiter().GetResult();
    }

    private void AppendError(string runId, string message) => GetRun(runId)?.AppendError(message);

    private void UpsertStep(string runId, string stepId, Action<RunStepEntry> configure) => GetRun(runId)?.UpsertStep(stepId, configure);

    private void SetRunControl(string runId, RunControl control)
    {
        var ctx = GetRun(runId);
        if (ctx is not null) ctx.Control = control;
    }

    private RunControl? GetRunControl(string runId) => GetRun(runId)?.Control;

    private void AppendStepLog(string runId, string stepId, string message) => GetRun(runId)?.AppendStepLog(stepId, message);

    private void UpsertTransition(string runId, RunStepTransitionSnapshot transition) => GetRun(runId)?.UpsertTransition(transition);

    private void AddTransition(string runId, RunStepTransitionSnapshot transition) => GetRun(runId)?.AddTransition(transition);

    /// <summary>The shared instance of a node type, to read what the type is. Never run a step on it — see <see cref="INodeFactory"/>.</summary>
    public Node? FindNode(string nodeType) => _nodeFactory.GetPrototype(nodeType);

    /// <summary>A new node of the type, free to be changed or thrown away (e.g. to reconfigure its inputs at design time).</summary>
    public Node? CreateNode(string nodeType) => _nodeFactory.TryCreate(nodeType, out Node node) ? node : null;

    // ──────────────────────────────────────────────────────────────────────
    // Public execute entry points
    // ──────────────────────────────────────────────────────────────────────

    public async ValueTask<OperationResult<WorkflowRunResult>> ExecuteAsync(string workflowId, IDictionary<string, object?>? input, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid();
        try
        {
            return await ExecuteInternalAsync(runId, workflowId, input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = new WorkflowExecutionException(workflowId, runId, null, $"Workflow '{workflowId}' execution failed.", ex);
            _logger.LogError(ex, "Workflow {WorkflowId} execution failed with unhandled exception.", workflowId);

            SetRunState(runId.ToString("D"), RunStatus.Failed, error.Message);
            AppendError(runId.ToString("D"), error.Message);
            await _telemetryEmitter.EmitAsync(BuildCorrelationContext(runId, workflowId), new RunFailedEvent(workflowId, DateTime.UtcNow, DateTime.UtcNow, error.Message), cancellationToken).ConfigureAwait(false);

            return OperationResult<WorkflowRunResult>.Failure(error.Message);
        }
    }

    /// <summary>
    /// Surcharge non-breaking acceptant un <see cref="RunContext"/> parent.
    /// Propage le <c>TraceId</c> et le <c>SpanId</c> depuis le contexte parent,
    /// ce qui permet de reconstruire la hiérarchie OTel complète quand ce workflow
    /// est appelé depuis une étape qui appelle la bibliothèque (<c>LibraryCallNode</c>) ou un <c>WorkflowExecutable</c>.
    /// </summary>
    public async ValueTask<OperationResult<WorkflowRunResult>> ExecuteAsync(string workflowId, IDictionary<string, object?>? input, CancellationToken cancellationToken, RunContext? parentContext)
    {
        var runId = Guid.NewGuid();
        try
        {
            return await ExecuteInternalAsync(runId, workflowId, input, cancellationToken, parentContext).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = new WorkflowExecutionException(workflowId, runId, null, $"Workflow '{workflowId}' execution failed.", ex);
            _logger.LogError(ex, "Workflow {WorkflowId} execution failed with unhandled exception.", workflowId);

            SetRunState(runId.ToString("D"), RunStatus.Failed, error.Message);
            AppendError(runId.ToString("D"), error.Message);
            await _telemetryEmitter.EmitAsync(BuildCorrelationContext(runId, workflowId, parentContext: parentContext), new RunFailedEvent(workflowId, DateTime.UtcNow, DateTime.UtcNow, error.Message), cancellationToken).ConfigureAwait(false);

            return OperationResult<WorkflowRunResult>.Failure(error.Message);
        }
    }

    public async ValueTask<OperationResult<WorkflowRunResult>> ExecuteAsync(Guid runId, string workflowId, IDictionary<string, object?>? input, CancellationToken cancellationToken = default, RunContext? parentContext = null)
    {
        try
        {
            return await ExecuteInternalAsync(runId, workflowId, input, cancellationToken, parentContext).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = new WorkflowExecutionException(workflowId, runId, null, $"Workflow '{workflowId}' execution failed.", ex);
            _logger.LogError(ex, "Workflow {WorkflowId} execution failed with unhandled exception.", workflowId);

            SetRunState(runId.ToString("D"), RunStatus.Failed, error.Message);
            AppendError(runId.ToString("D"), error.Message);
            await _telemetryEmitter.EmitAsync(BuildCorrelationContext(runId, workflowId), new RunFailedEvent(workflowId, DateTime.UtcNow, DateTime.UtcNow, error.Message), cancellationToken).ConfigureAwait(false);

            return OperationResult<WorkflowRunResult>.Failure(error.Message);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Resume after human-in-the-loop interaction
    // ──────────────────────────────────────────────────────────────────────

    public ValueTask<SuspendedRunContext?> ProvideInteractionResponseAsync(Guid runId, InteractionResponse response, CancellationToken cancellationToken = default) =>
        TakeUpWaitingRunAsync(runId, response ?? throw new ArgumentNullException(nameof(response)), cancellationToken);

    /// <summary>
    /// Takes up again a workflow run that something else called (a team, a step of another workflow) and that stopped to
    /// wait. This is the one place where that is done.
    /// <para>
    /// A run that asked a person something goes on with their answer, read where answers are kept
    /// (<paramref name="interactions"/>); while there is none, it is still waiting. A run that waits for anything else simply
    /// runs its waiting step again: that step knows what it was waiting for.
    /// </para>
    /// </summary>
    /// <returns>
    /// What the run came to (it may have stopped to wait again), or <c>null</c> with <c>WaitingForToken</c> when what it
    /// waits for has not come yet.
    /// </returns>
    public async ValueTask<(OperationResult<WorkflowRunResult>? Result, string WaitingForToken)> ResumeNestedAsync(Guid runId, RunContext context, IInteractionRequestStore? interactions, CancellationToken cancellationToken = default)
    {
        var run = GetRun(runId.ToString("D"));

        // The run has ended since it was last called: it is not run again, what it came to is given back. That is how the
        // run that called it goes on once it is taken up again in its turn.
        if (run is { Status: RunStatus.Completed })
            return (await ResultOfFinishedRunAsync(run, cancellationToken).ConfigureAwait(false), string.Empty);
        if (run is { Status: RunStatus.Failed or RunStatus.Cancelled })
            return (OperationResult<WorkflowRunResult>.Failure(run.Error ?? $"Workflow run '{runId}' ended as {run.Status}."), string.Empty);

        if (run?.Payload is not { } payload)
            return (OperationResult<WorkflowRunResult>.Failure($"Workflow run '{runId}' could not be resumed (suspended state not found or already consumed)."), string.Empty);

        var waiting = WorkflowProgress.FromRecord(payload);
        InteractionResponse? response = null;
        if (waiting.InteractionRequestId != Guid.Empty)
        {
            response = interactions is null ? null : await interactions.GetResponseAsync(waiting.InteractionRequestId, cancellationToken).ConfigureAwait(false);
            if (response is null)
                return (null, waiting.AwaitedToken);
        }

        var suspended = await TakeUpWaitingRunAsync(runId, response, cancellationToken).ConfigureAwait(false);
        if (suspended is null)
            return (OperationResult<WorkflowRunResult>.Failure($"Workflow run '{runId}' could not be resumed (suspended state not found or already consumed)."), waiting.AwaitedToken);

        return (await ResumeAfterInteractionAsync(suspended, cancellationToken, parentContext: context).ConfigureAwait(false), waiting.AwaitedToken);
    }

    /// <summary>
    /// A run that another run called keeps what it came to, in the run itself, before it is marked finished: the run that
    /// called it may only be taken up again later, and then reads it there instead of running the workflow a second time.
    /// It takes the place of the point the run had stopped on, which no longer means anything.
    /// </summary>
    private void KeepWhatItProduced(string runId, Dictionary<string, object?> finalState, IEnumerable<string> executedNodes, RunContext? parentContext)
    {
        if (parentContext is null || GetRun(runId) is not { } run)
            return;

        var produced = new WorkflowProgress(SuspendedNodeId: string.Empty, InteractionRequestId: Guid.Empty, State: WorkflowStateSerializer.SerializeState(finalState), ExecutedNodes: [.. executedNodes], VisitedNodes: [], Pending: [], RemainingPredecessors: [], SuspendedAt: DateTime.UtcNow);
        run.SetPayload("workflow", produced.ToRecord());
        _runRepository.AddAsync(run).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>What a finished run produced, read from the run itself (see <see cref="KeepWhatItProduced"/>).</summary>
    private async ValueTask<OperationResult<WorkflowRunResult>> ResultOfFinishedRunAsync(RunContext run, CancellationToken cancellationToken)
    {
        var definition = run.EntityId is null ? null : await _workflowService.GetByIdAsync(run.EntityId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
            return OperationResult<WorkflowRunResult>.Failure($"Workflow '{run.EntityId}' of run '{run.RunId}' not found.");

        if (run.Payload is not { } payload || WorkflowProgress.FromRecord(payload) is not { SuspendedNodeId.Length: 0 } produced)
            return OperationResult<WorkflowRunResult>.Failure($"Workflow run '{run.RunId}' has finished, and what it produced was not kept: it cannot be given back.");

        return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, WorkflowStateSerializer.DeserializeState(produced.State), produced.ExecutedNodes));
    }

    /// <summary>
    /// Puts a waiting run back to running at the step it stopped on. <paramref name="response"/> is the person's answer when
    /// the step was waiting for one: it is left for the step under its name.
    /// </summary>
    public async ValueTask<SuspendedRunContext?> TakeUpWaitingRunAsync(Guid runId, InteractionResponse? response, CancellationToken cancellationToken = default)
    {
        // Atomically transition WaitingForInput → Running
        var entry = GetRun(runId.ToString("D"));
        if (entry is null || entry.Status != RunStatus.WaitingForInput)
        {
            return null;
        }

        SetRunState(runId.ToString("D"), RunStatus.Running);

        Notify(runId, WorkflowRunStreamNotifications.RunResumed(runId));

        // The DAG-walk progress (checkpointed at HIL suspension) lives in the run's own
        // RunRecord.Payload — no separate suspended-run store anymore.
        var workflowId = entry.EntityId;
        if (workflowId is null || entry.Payload is not { } payload)
        {
            _logger.LogWarning("Suspended run {RunId} has no progress payload", runId);
            SetRunState(runId.ToString("D"), RunStatus.WaitingForInput);
            Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "waiting_for_input_restored", state: WorkflowRunState.WaitingForInput.ToString()));
            return null;
        }
        var persisted = WorkflowProgress.FromRecord(payload);

        var definition = await _workflowService.GetByIdAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            _logger.LogError("Cannot resume run {RunId}: workflow '{WorkflowId}' not found", runId, workflowId);
            FailRun(runId, workflowId, persisted.SuspendedNodeId, $"Cannot resume: workflow '{workflowId}' not found");
            return null;
        }

        var state = WorkflowStateSerializer.DeserializeState(persisted.State);
        var executedNodes = persisted.ExecutedNodes;
        var visitedNodes = new HashSet<string>(persisted.VisitedNodes, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(persisted.Pending);
        var remainingPredecessors = new Dictionary<string, int>(persisted.RemainingPredecessors, StringComparer.OrdinalIgnoreCase);

        var runControl = new RunControl();

        if (response is not null)
            state[$"__interaction_response_{persisted.SuspendedNodeId}"] = response;

        SetRunControl(runId.ToString("D"), runControl);

        return new SuspendedRunContext(runId, definition, persisted.SuspendedNodeId, persisted.InteractionRequestId, state, executedNodes, visitedNodes, pending, remainingPredecessors, runControl);
    }

    public async ValueTask<OperationResult<WorkflowRunResult>> ResumeAfterInteractionAsync(SuspendedRunContext suspended, CancellationToken cancellationToken = default, RunContext? parentContext = null)
    {
        var runId = suspended.RunId;
        var runControl = suspended.RunControl;

        try
        {
            return await ResumeExecutionLoopAsync(runId, suspended.Definition, suspended.State, suspended.ExecutedNodes, suspended.VisitedNodes, suspended.Pending, suspended.RemainingPredecessors, runControl, suspended.SuspendedNodeId, cancellationToken, parentContext).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow {RunId} resume failed.", runId);
            FailRun(runId, suspended.Definition.Id, suspended.SuspendedNodeId, $"Resume failed: {ex.Message}", exception: ex);
            return OperationResult<WorkflowRunResult>.Failure(ex.Message);
        }
        finally
        {
            var isSuspended = GetRun(runId.ToString("D"))?.Status == RunStatus.WaitingForInput;
            if (!isSuspended)
            {
                runControl.Dispose();
            }
        }
    }

    /// <summary>
    /// Publishes a workflow stream notification to the unified <see cref="IRunEventBus"/>.
    /// The workflowId is resolved from the registry; falls back to empty string if the run is unknown.
    /// </summary>
    private void Notify(Guid runId, WorkflowRunStreamNotification notification, bool terminal = false)
    {
        if (_eventBus is null)
            return;
        var runIdStr = runId.ToString("D");
        _runWorkflowIds.TryGetValue(runIdStr, out var workflowId);
        var execution = GetRun(runIdStr)?.Execution;
        WorkflowRunEventBusPublisher.Publish(_eventBus, runId, workflowId ?? string.Empty, notification, terminal, execution?.ExecutionId, execution?.StreamId);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Failure helper
    // ──────────────────────────────────────────────────────────────────────

    private void FailRun(Guid runId, string workflowId, string? nodeId, string message, Dictionary<string, object?>? state = null, string? nodeType = null, Exception? exception = null)
    {
        _logger.LogError(exception, "Workflow run {RunId} failed at node {NodeId}: {Message}", runId, nodeId, message);

        SetRunState(runId.ToString("D"), RunStatus.Failed, message);
        AppendError(runId.ToString("D"), message);
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            UpsertStep(
                runId.ToString("D"),
                nodeId,
                step =>
                {
                    step.Status = RunStatus.Failed;
                    step.Error = message;
                }
            );
            Notify(runId, WorkflowRunStreamNotifications.NodeUpdated(runId, nodeId, WorkflowNodeRunState.Failed.ToString()));
        }

        Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "failed", state: WorkflowRunState.Failed.ToString()), terminal: true);

        _ = EmitFailureTelemetryAsync(runId, workflowId, GetRun(runId.ToString("D"))?.CreatedAt.DateTime ?? DateTime.UtcNow, nodeId, nodeType, state, message);
    }

    // ── State mapping helpers ──────────────────────────────────────────────

    internal static RunStatus MapToRunState(WorkflowRunState s) =>
        s switch
        {
            WorkflowRunState.Running => RunStatus.Running,
            WorkflowRunState.Paused => RunStatus.Paused,
            WorkflowRunState.WaitingForInput => RunStatus.WaitingForInput,
            WorkflowRunState.Completed => RunStatus.Completed,
            WorkflowRunState.Failed => RunStatus.Failed,
            WorkflowRunState.Cancelled => RunStatus.Cancelled,
            _ => RunStatus.Running,
        };

    internal static WorkflowRunState MapToWorkflowRunState(RunStatus s) =>
        s switch
        {
            RunStatus.Running => WorkflowRunState.Running,
            RunStatus.Paused => WorkflowRunState.Paused,
            RunStatus.WaitingForInput => WorkflowRunState.WaitingForInput,
            RunStatus.Completed => WorkflowRunState.Completed,
            RunStatus.Failed => WorkflowRunState.Failed,
            RunStatus.Cancelled => WorkflowRunState.Cancelled,
            _ => WorkflowRunState.Running,
        };
}
