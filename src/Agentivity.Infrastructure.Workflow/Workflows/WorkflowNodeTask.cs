using Agentivity.Domain.Run;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// A <see cref="GraphTask"/> that wraps the execution of a single workflow node.
/// </summary>
/// <remarks>
/// Control-flow nodes (<see cref="IControlFlowNode"/>) return
/// <see cref="GraphTaskResult.Suspended"/> with reason <c>"control_flow"</c>.
/// <see cref="WorkflowEngine"/> detects this suspension and handles sub-graph
/// re-submission, preserving existing control-flow semantics above the graph layer.
///
/// <see cref="WorkflowSuspendedException"/> (human-in-the-loop) also returns a
/// suspended result with reason <c>"human_loop"</c>; the engine persists the
/// suspended run context and returns early.
/// </remarks>
internal sealed class WorkflowNodeTask : GraphTask
{
    private readonly WorkflowEntity _definition;
    private readonly WorkflowNodeDefinition _node;
    private readonly Node _workflowNode;
    private readonly Dictionary<string, object?> _state;
    private readonly Guid _runId;
    private readonly string _runIdStr;
    private readonly IRepository<RunContext, RunRecord> _runRepository;
    private readonly IAgentTelemetryEmitter _telemetryEmitter;
    private readonly ISystemResourceMonitor _resourceMonitor;
    private readonly ILogger _logger;

    /// <summary>
    /// Carries the handler result after successful completion (non-null).
    /// Available to <see cref="WorkflowEngine"/> after <see cref="GraphTaskResult.Completed"/> is returned.
    /// </summary>
    internal IDictionary<string, object?>? HandlerResult { get; private set; }

    /// <summary>
    /// Carries the control-flow result when <c>reason == "control_flow"</c>.
    /// </summary>
    internal ControlFlowResult? ControlFlowResult { get; private set; }

    /// <summary>
    /// Carries the suspension request when <c>reason == "human_loop"</c>.
    /// </summary>
    internal WorkflowWaitException? SuspendedBy { get; private set; }

    // ── Sync bridging helpers over the async IRepository surface ────────────
    // FindAsync's cache-hit path is synchronous under the hood (ConcurrentDictionary
    // lookup, no I/O) — same reasoning applied elsewhere in this codebase (WorkflowEngine.cs).

    private RunContext? GetRun(string runId) => _runRepository.FindAsync(runId).AsTask().GetAwaiter().GetResult();

    private void UpsertStep(string runId, string stepId, Action<RunStepEntry> configure) => GetRun(runId)?.UpsertStep(stepId, configure);

    private RunControl? GetRunControl(string runId) => GetRun(runId)?.Control;

    private void AppendStepLog(string runId, string stepId, string message) => GetRun(runId)?.AppendStepLog(stepId, message);

    private DebugTraceItem? GetStepTrace(string runId, string stepId) => GetRun(runId)?.GetStepTrace(stepId);

    private void SetStepTrace(string runId, string stepId, DebugTraceItem? trace) => GetRun(runId)?.SetStepTrace(stepId, trace);

    internal WorkflowNodeTask(
        WorkflowEntity definition,
        WorkflowNodeDefinition node,
        Node WorkflowNodeDefinition,
        Dictionary<string, object?> state,
        Guid runId,
        IRepository<RunContext, RunRecord> runRepository,
        IAgentTelemetryEmitter telemetryEmitter,
        ILogger logger,
        string[] dependsOn,
        ISystemResourceMonitor? resourceMonitor = null
    )
        : base(node.Id, dependsOn)
    {
        _definition = definition;
        _node = node;
        _workflowNode = WorkflowNodeDefinition;
        _state = state;
        _runId = runId;
        _runIdStr = runId.ToString("D");
        _runRepository = runRepository;
        _telemetryEmitter = telemetryEmitter;
        _resourceMonitor = resourceMonitor ?? NullSystemResourceMonitor.Instance;
        _logger = logger;
    }

    public override async ValueTask<GraphTaskResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_workflowNode is ResourceNode)
        {
            return GraphTaskResult.Completed(null);
        }

        // Adaptive throttle: delay node submission when system is under CPU/memory pressure.
        var throttleDelay = _resourceMonitor.ComputeThrottleDelay();
        if (throttleDelay > TimeSpan.Zero)
            await Task.Delay(throttleDelay, cancellationToken).ConfigureAwait(false);

        // Record started
        UpsertStep(
            _runIdStr,
            _node.Id,
            step =>
            {
                step.Name = _node.Id;
                step.Status = RunStatus.Running;
            }
        );

        _logger.LogDebug("Executing workflow node {Workflow}:{NodeId} ({Handler})", _definition.Name, _node.Id, _workflowNode.Name);

        var config = new NodeConfig(_definition, _node, _state);
        var runExecution = GetRun(_runIdStr)?.Execution;
        var services = new WorkflowRunServices(
            runId: _runId,
            checkpointAsync: async ct =>
            {
                if (GetRunControl(_runIdStr) is { } ctrl)
                    await ctrl.CheckpointAsync(ct).ConfigureAwait(false);
            },
            publishStatus: msg => AppendStepLog(_runIdStr, _node.Id, msg),
            updateTrace: updater =>
            {
                var accumulator = new Agentivity.Infrastructure.Core.Diagnostics.DebugTraceAccumulator(GetStepTrace(_runIdStr, _node.Id));
                updater(accumulator);
                SetStepTrace(_runIdStr, _node.Id, accumulator.Build());
            },
            telemetryEmitter: _telemetryEmitter,
            correlationContext: BuildCorrelationContext(_runId, _definition.Id, _node.Id),
            logger: _logger,
            executionId: runExecution?.ExecutionId,
            streamId: runExecution?.StreamId
        );

        try
        {
            config.ResolveAll();
        }
        catch (Exception ex)
        {
            var msg = $"Failed to resolve inputs for node '{_node.Id}': {ex.Message}";
            _logger.LogError(ex, "Input resolution failed for node {NodeId}", _node.Id);
            return Failed(msg);
        }

        // Store resolved inputs in the registry so the inspector can display them.
        if (config.ResolvedInputs is { Count: > 0 } resolvedInputs)
        {
            UpsertStep(_runIdStr, _node.Id, step => step.Inputs = new Dictionary<string, object?>(resolvedInputs, StringComparer.OrdinalIgnoreCase));
        }

        // --- Control-flow nodes ---
        if (_workflowNode is IControlFlowNode controlFlowNode)
        {
            ControlFlowResult cfResult;
            try
            {
                cfResult = await controlFlowNode.EvaluateAsync(config, services, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return GraphTaskResult.Failed($"Control flow evaluation failed for node '{_node.Id}': {ex.Message}");
            }

            ControlFlowResult = cfResult;
            return GraphTaskResult.Suspended("control_flow");
        }

        // --- Standard node execution ---
        IDictionary<string, object?>? handlerResult;
        try
        {
            handlerResult = await _workflowNode.ExecuteAsync(config, services, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowWaitException suspended)
        {
            SuspendedBy = suspended; // kept for backward compat (e.g. tests inspecting the property)
            // Record in the shared context so WorkflowExecutionStore can persist the HIL state
            // when GraphHost calls SaveAsync after catching the IGraphSignalException.
            try
            {
                Context<WorkflowGraphContext>().RecordHilSuspension(_node.Id, suspended);
            }
            catch
            { /* context not available in isolation */
            }
            throw; // propagate as IGraphSignalException → GraphHost handles persistence
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed($"Execution of node '{_node.Id}' failed: {ex.Message}");
        }

        if (handlerResult is null)
        {
            return Failed($"Node '{_node.Id}' returned no result.");
        }

        HandlerResult = handlerResult;
        return GraphTaskResult.Completed(handlerResult);
    }

    /// <summary>The step failed: it goes on by its Error exit when that exit leads somewhere, otherwise the workflow stops (<see cref="StepExits"/>).</summary>
    private GraphTaskResult Failed(string message)
    {
        if (StepExits.GoOnByError(_definition, _node.Id, message) is not { } byError)
            return GraphTaskResult.Failed(message);

        HandlerResult = byError;
        return GraphTaskResult.Completed(byError);
    }

    private static ExecutionCorrelationContext BuildCorrelationContext(Guid runId, string workflowId, string? nodeId = null) =>
        new(RunId: runId, EntityId: workflowId, TraceId: runId.ToString("N"), SpanId: string.IsNullOrWhiteSpace(nodeId) ? workflowId : nodeId, ParentSpanId: string.IsNullOrWhiteSpace(nodeId) ? null : workflowId, NodeExecutionId: nodeId);
}
