using Agentivity.Domain.Common;
using Agentivity.Domain.Run;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.Abstractions;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Graph orchestrator that drives a full workflow execution as a single <see cref="GraphExecution"/>.
/// Each workflow node becomes a <see cref="WorkflowNodeTask"/>; this orchestrator decides which
/// tasks to submit next based on the node dependency graph and the results of completed tasks.
/// </summary>
internal sealed class WorkflowGraphOrchestrator : GraphOrchestrator
{
    // ── Shared context ────────────────────────────────────────────────────────
    private readonly WorkflowGraphContext _context;

    // ── Workflow definition ───────────────────────────────────────────────────
    private readonly WorkflowEntity _definition;
    // Where nodes come from: Create(...) for a node to run a step with (new each time), GetPrototype(...) to ask about a type.
    private readonly INodeFactory _nodes;

    // ── Execution tracking ────────────────────────────────────────────────────
    private readonly Dictionary<string, int> _remainingPredecessors;
    private readonly Dictionary<string, WorkflowNodeTask> _submittedTasks = new(StringComparer.OrdinalIgnoreCase);

    // ── Run metadata ──────────────────────────────────────────────────────────
    private readonly Guid _runId;
    private readonly string _runIdStr;
    private readonly string _workflowId;
    private readonly DateTime _runStartedAtUtc;
    private readonly RunContext? _parentContext;

    // ── Infrastructure ────────────────────────────────────────────────────────
    private readonly IRepository<RunContext, RunRecord> _runRepository;
    private readonly IAgentTelemetryEmitter _telemetryEmitter;
    private readonly ISystemResourceMonitor _resourceMonitor;
    private readonly ILogger _logger;

    // ── Initial nodes (used for resume) ──────────────────────────────────────
    private readonly IReadOnlyList<string>? _initialNodeIds;

    // ── Delegates back to WorkflowEngine private methods ─────────────────────
    private readonly Func<Guid, WorkflowRunStreamNotification, bool, ValueTask> _notifyAsync;
    private readonly Func<Guid, string, string, string, Dictionary<string, object?>, CancellationToken, ValueTask> _emitNodeStartedAsync;
    private readonly Func<Guid, string, string, string, Dictionary<string, object?>, CancellationToken, ValueTask> _emitNodeCompletedAsync;

    private static readonly IReadOnlyDictionary<string, object?> EmptyDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    // ── GraphOrchestrator contract ────────────────────────────────────────────

    public override GraphExecutionContext Context => _context;

    // ── Public accessors ──────────────────────────────────────────────────────

    internal Guid RunId => _runId;
    internal string WorkflowId => _workflowId;
    internal WorkflowGraphContext WorkflowContext => _context;
    internal IReadOnlyList<string> ExecutedNodes => _context.ExecutedNodes;

    /// <summary>
    /// Why the run failed, when it failed without a node task failing — a control-flow node (a loop, a switch) that could not
    /// finish, or a node that returned nothing. The graph then simply has no next step and ends "completed"; whoever reads the
    /// graph's outcome must check this too, or such a run is reported as a success.
    /// </summary>
    internal string? Failure { get; private set; }


    internal IReadOnlyDictionary<string, int> GetRemainingPredecessors() => _remainingPredecessors;

    internal List<string> GetPendingNodeIds() => _remainingPredecessors.Where(kvp => kvp.Value == 0 && !_context.VisitedNodes.Contains(kvp.Key)).Select(kvp => kvp.Key).ToList();

    // ── Constructor ───────────────────────────────────────────────────────────

    internal WorkflowGraphOrchestrator(
        Guid runId,
        WorkflowEntity definition,
        INodeFactory nodes,
        Dictionary<string, int> remainingPredecessors,
        WorkflowGraphContext context,
        IRepository<RunContext, RunRecord> runRepository,
        IAgentTelemetryEmitter telemetryEmitter,
        ILogger logger,
        string workflowId,
        DateTime runStartedAtUtc,
        RunContext? parentContext,
        Func<Guid, WorkflowRunStreamNotification, bool, ValueTask> notifyAsync,
        Func<Guid, string, string, string, Dictionary<string, object?>, CancellationToken, ValueTask> emitNodeStartedAsync,
        Func<Guid, string, string, string, Dictionary<string, object?>, CancellationToken, ValueTask> emitNodeCompletedAsync,
        IReadOnlyList<string>? initialNodeIds = null,
        ISystemResourceMonitor? resourceMonitor = null
    )
    {
        _runId = runId;
        _runIdStr = runId.ToString("D");
        _definition = definition;
        _nodes = nodes;
        _remainingPredecessors = new Dictionary<string, int>(remainingPredecessors, StringComparer.OrdinalIgnoreCase);
        _context = context;
        _runRepository = runRepository;
        _telemetryEmitter = telemetryEmitter;
        _logger = logger;
        _workflowId = workflowId;
        _runStartedAtUtc = runStartedAtUtc;
        _parentContext = parentContext;
        _notifyAsync = notifyAsync;
        _emitNodeStartedAsync = emitNodeStartedAsync;
        _emitNodeCompletedAsync = emitNodeCompletedAsync;
        _initialNodeIds = initialNodeIds;
        _resourceMonitor = resourceMonitor ?? NullSystemResourceMonitor.Instance;
    }

    // ── Sync bridging helpers over the async IRepository surface ────────────
    // FindAsync's cache-hit path is synchronous under the hood (ConcurrentDictionary
    // lookup, no I/O) — same reasoning applied elsewhere in this codebase (WorkflowEngine.cs).

    private RunContext? GetRun(string runId) => _runRepository.FindAsync(runId).AsTask().GetAwaiter().GetResult();

    private void UpsertStep(string runId, string stepId, Action<RunStepEntry> configure) => GetRun(runId)?.UpsertStep(stepId, configure);

    private void AppendError(string runId, string message) => GetRun(runId)?.AppendError(message);

    private void UpsertTransition(string runId, RunStepTransitionSnapshot transition) => GetRun(runId)?.UpsertTransition(transition);

    private RunControl? GetRunControl(string runId) => GetRun(runId)?.Control;

    private void AppendStepLog(string runId, string stepId, string message) => GetRun(runId)?.AppendStepLog(stepId, message);

    // ── GraphOrchestrator.OnStartAsync ────────────────────────────────────────

    public override async ValueTask<IReadOnlyList<GraphTask>> OnStartAsync(GraphSession session, CancellationToken ct)
    {
        var tasks = new List<GraphTask>();

        if (_initialNodeIds is { Count: > 0 })
        {
            // Resume mode: caller specifies which nodes to start with
            foreach (var nodeId in _initialNodeIds)
            {
                var task = await CreateAndEmitNodeTaskAsync(nodeId, [], ct).ConfigureAwait(false);
                if (task is not null)
                    tasks.Add(task);
            }
        }
        else
        {
            // Normal mode: start with all nodes that have no predecessors
            foreach (var kvp in _remainingPredecessors)
            {
                if (kvp.Value == 0 && !_context.VisitedNodes.Contains(kvp.Key))
                {
                    var task = await CreateAndEmitNodeTaskAsync(kvp.Key, [], ct).ConfigureAwait(false);
                    if (task is not null)
                        tasks.Add(task);
                }
            }

            // Ensure the entry node comes first if specified
            if (!string.IsNullOrWhiteSpace(_definition.EntryNode) && tasks.Count > 1)
            {
                tasks = tasks.OrderByDescending(t => string.Equals(t.Id, _definition.EntryNode, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        return tasks;
    }

    // ── GraphOrchestrator.OnTaskCompletedAsync ────────────────────────────────

    public override async ValueTask<IReadOnlyList<GraphTask>?> OnTaskCompletedAsync(string taskId, GraphTaskStatus status, object? output, string? error, GraphSession session, CancellationToken ct)
    {
        if (!_submittedTasks.TryGetValue(taskId, out var nodeTask))
        {
            _logger.LogWarning("OnTaskCompletedAsync: task '{TaskId}' not found in submitted tasks", taskId);
            return null;
        }

        var state = _context.State;

        // ── Failed ────────────────────────────────────────────────────────────
        if (status == GraphTaskStatus.Failed)
        {
            var errMsg = error ?? $"Node '{taskId}' failed.";
            UpsertStep(
                _runIdStr,
                taskId,
                step =>
                {
                    step.Status = RunStatus.Failed;
                    step.Error = errMsg;
                }
            );
            AppendError(_runIdStr, errMsg);
            await _notifyAsync(_runId, WorkflowRunStreamNotifications.NodeUpdated(_runId, taskId, WorkflowNodeRunState.Failed.ToString()), false).ConfigureAwait(false);
            await _notifyAsync(_runId, WorkflowRunStreamNotifications.RunUpdated(_runId, reason: "failed", state: WorkflowRunState.Failed.ToString()), true).ConfigureAwait(false);
            return null;
        }

        // ── Suspended — control_flow ──────────────────────────────────────────
        if (status == GraphTaskStatus.Suspended && nodeTask.ControlFlowResult is not null)
        {
            var controlResult = nodeTask.ControlFlowResult;

            // Find the node definition
            var nodeDef = _definition.FindNode(taskId);
            if (nodeDef is null)
            {
                _logger.LogError("Control flow node '{NodeId}' not found in definition", taskId);
                return null;
            }

            if (_nodes.GetPrototype(nodeDef.NodeType) is not IControlFlowNode)
            {
                _logger.LogError("Node '{NodeId}' is not a control flow node", taskId);
                return null;
            }

            // Store any outputs from the control flow evaluation
            if (controlResult.Outputs is not null)
            {
                foreach (var pair in controlResult.Outputs)
                {
                    state[$"{taskId}.{pair.Key}"] = pair.Value;
                }
            }

            OperationResult<WorkflowRunResult> cfOpResult;
            switch (controlResult.Type)
            {
                case ControlFlowType.Loop:
                    cfOpResult = await ExecuteLoopAsync(taskId, controlResult, ct).ConfigureAwait(false);
                    break;

                case ControlFlowType.LoopComplete:
                case ControlFlowType.Continue:
                case ControlFlowType.Branch:
                    var resultOutputs = new Dictionary<string, object?>(controlResult.Outputs ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase) { ["__control_flow_type"] = controlResult.Type.ToString(), ["__exit_port"] = controlResult.ExitPort ?? controlResult.CompletePort };
                    cfOpResult = OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(_definition, resultOutputs, _context.ExecutedNodes));
                    break;

                case ControlFlowType.Skip:
                    _logger.LogDebug("Skipping downstream nodes for {NodeId}", taskId);
                    var skipOutputs = new Dictionary<string, object?>(controlResult.Outputs ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase);
                    cfOpResult = OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(_definition, skipOutputs, _context.ExecutedNodes));
                    break;

                default:
                    _logger.LogError("Unknown control flow type: {Type}", controlResult.Type);
                    return null;
            }

            if (cfOpResult.IsFailure)
            {
                var cfErr = cfOpResult.Error ?? "Control flow failed";
                UpsertStep(
                    _runIdStr,
                    taskId,
                    step =>
                    {
                        step.Status = RunStatus.Failed;
                        step.Error = cfErr;
                    }
                );
                AppendError(_runIdStr, cfErr);
                Failure ??= cfErr;
                await _notifyAsync(_runId, WorkflowRunStreamNotifications.NodeUpdated(_runId, taskId, WorkflowNodeRunState.Failed.ToString()), false).ConfigureAwait(false);
                await _notifyAsync(_runId, WorkflowRunStreamNotifications.RunUpdated(_runId, reason: "failed", state: WorkflowRunState.Failed.ToString()), true).ConfigureAwait(false);
                return null;
            }

            _context.ExecutedNodes.Add(taskId);
            _context.VisitedNodes.Add(taskId);

            var controlOutputs = cfOpResult.Value?.State ?? new Dictionary<string, object?>();

            foreach (var pair in controlOutputs)
            {
                if (!pair.Key.StartsWith("__"))
                {
                    state[$"{taskId}.{pair.Key}"] = pair.Value;
                }
            }

            var controlOutputsTyped = new Dictionary<string, object?>(controlOutputs, StringComparer.OrdinalIgnoreCase);
            UpsertStep(
                _runIdStr,
                taskId,
                step =>
                {
                    step.Status = RunStatus.Completed;
                    step.Outputs = controlOutputsTyped;
                }
            );
            await _notifyAsync(_runId, WorkflowRunStreamNotifications.NodeUpdated(_runId, taskId, WorkflowNodeRunState.Completed.ToString()), false).ConfigureAwait(false);
            await _emitNodeCompletedAsync(_runId, _workflowId, taskId, nodeDef.NodeType, state, ct).ConfigureAwait(false);

            // Enqueue successors via exit_port
            var nextTasks = await EnqueueNextNodesForControlFlowAsync(taskId, controlOutputsTyped, ct).ConfigureAwait(false);
            return nextTasks.Count > 0 ? nextTasks : Array.Empty<GraphTask>();
        }

        // ── Completed — standard node ─────────────────────────────────────────
        var handlerResult = nodeTask.HandlerResult;
        if (handlerResult is null)
        {
            _logger.LogWarning("Node '{NodeId}' completed but returned no result", taskId);
            var noResultErr = $"Node '{taskId}' returned no result.";
            UpsertStep(
                _runIdStr,
                taskId,
                step =>
                {
                    step.Status = RunStatus.Failed;
                    step.Error = noResultErr;
                }
            );
            AppendError(_runIdStr, noResultErr);
            Failure ??= noResultErr;
            await _notifyAsync(_runId, WorkflowRunStreamNotifications.RunUpdated(_runId, reason: "failed", state: WorkflowRunState.Failed.ToString()), true).ConfigureAwait(false);
            return null;
        }

        // Write outputs into shared state (not the exit the step chose)
        foreach (var pair in handlerResult)
        {
            if (!pair.Key.StartsWith("__", StringComparison.Ordinal))
                state[$"{taskId}.{pair.Key}"] = pair.Value;
        }

        _context.ExecutedNodes.Add(taskId);
        _context.VisitedNodes.Add(taskId);

        var exits = handlerResult.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
        var outputs = exits.Where(kvp => !kvp.Key.StartsWith("__", StringComparison.Ordinal)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        // A step that failed and went on by its Error exit is shown as failed; the workflow goes on all the same.
        var failed = StepExits.WentByError(exits);

        UpsertStep(
            _runIdStr,
            taskId,
            step =>
            {
                step.Status = failed ? RunStatus.Failed : RunStatus.Completed;
                step.Error = failed ? outputs[StepExits.Error]?.ToString() : step.Error;
                step.Outputs = outputs;
            }
        );
        await _notifyAsync(_runId, WorkflowRunStreamNotifications.NodeUpdated(_runId, taskId, (failed ? WorkflowNodeRunState.Failed : WorkflowNodeRunState.Completed).ToString()), false).ConfigureAwait(false);

        // Resolve node type for telemetry
        var completedNodeDef = _definition.FindNode(taskId);
        var completedNodeType = completedNodeDef?.NodeType ?? taskId;
        await _emitNodeCompletedAsync(_runId, _workflowId, taskId, completedNodeType, state, ct).ConfigureAwait(false);

        // Find and submit ready successors, by the exits the step goes on by
        var successorTasks = await GetReadySuccessorsAsync(taskId, exits, ct).ConfigureAwait(false);
        return successorTasks.Count > 0 ? successorTasks : Array.Empty<GraphTask>();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="WorkflowNodeTask"/> for the given node, emits node-started telemetry,
    /// and registers the task in <see cref="_submittedTasks"/>.
    /// Returns null for resource nodes (they are skipped and marked visited).
    /// </summary>
    private async ValueTask<WorkflowNodeTask?> CreateAndEmitNodeTaskAsync(string nodeId, string[] dependsOn, CancellationToken ct)
    {
        var nodeDef = _definition.FindNode(nodeId);
        if (nodeDef is null)
        {
            _logger.LogWarning("Node '{NodeId}' not found in workflow definition", nodeId);
            return null;
        }

        if (_nodes.GetPrototype(nodeDef.NodeType) is not { } nodeKind)
        {
            _logger.LogWarning("No node registered with type '{NodeType}' for node '{NodeId}'", nodeDef.NodeType, nodeId);
            return null;
        }

        if (nodeKind is ResourceNode)
        {
            // Resource nodes are skipped — mark as visited immediately
            _context.VisitedNodes.Add(nodeId);
            return null;
        }

        // Tell the client this node is now running (STEP_STARTED) — the loop-body path already does; without it a normal node was only ever announced once it had finished.
        await _notifyAsync(_runId, WorkflowRunStreamNotifications.NodeUpdated(_runId, nodeDef.Id, WorkflowNodeRunState.Running.ToString()), false).ConfigureAwait(false);
        // Emit node-started telemetry before the task is scheduled
        await _emitNodeStartedAsync(_runId, _workflowId, nodeDef.Id, nodeDef.NodeType, _context.State, ct).ConfigureAwait(false);

        var nodeImpl = _nodes.Create(nodeDef.NodeType); // a new node for this execution of the step — never one that served another
        var task = new WorkflowNodeTask(_definition, nodeDef, nodeImpl, _context.State, _runId, _runRepository, _telemetryEmitter, _logger, dependsOn: dependsOn, resourceMonitor: _resourceMonitor);

        _submittedTasks[nodeId] = task;
        return task;
    }

    /// <summary>Creates a <see cref="WorkflowNodeTask"/> synchronously without telemetry emission. Used for loop body nodes.</summary>
    private WorkflowNodeTask? CreateNodeTask(string nodeId, string[] dependsOn)
    {
        var nodeDef = _definition.FindNode(nodeId);
        if (nodeDef is null)
        {
            _logger.LogWarning("Node '{NodeId}' not found in workflow definition", nodeId);
            return null;
        }

        if (_nodes.GetPrototype(nodeDef.NodeType) is not { } nodeKind)
        {
            _logger.LogWarning("No node registered with type '{NodeType}' for node '{NodeId}'", nodeDef.NodeType, nodeId);
            return null;
        }

        if (nodeKind is ResourceNode)
        {
            _context.VisitedNodes.Add(nodeId);
            return null;
        }

        var nodeImpl = _nodes.Create(nodeDef.NodeType); // a new node for this execution of the step — never one that served another
        var task = new WorkflowNodeTask(_definition, nodeDef, nodeImpl, _context.State, _runId, _runRepository, null!, _logger, dependsOn: dependsOn);

        _submittedTasks[nodeId] = task;
        return task;
    }

    /// <summary>
    /// Returns non-resource outgoing connections from <paramref name="nodeId"/>.
    /// </summary>
    private IReadOnlyList<WorkflowConnection> GetOutgoingConnections(string nodeId) =>
        _definition.Connections.Where(c => string.Equals(c.From, nodeId, StringComparison.OrdinalIgnoreCase) && !string.Equals(c.FromPort, "resource", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.ToPort, "resource", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// A node is ready once every incoming connection has been traversed — except a node that merges
    /// conditional branches (<see cref="IPartialExecutionNode"/>, e.g. FirstOf): only one of its branches
    /// ever runs, so waiting for all of them would leave it, and everything after it, unreached forever.
    /// Such a node decides for itself from how many of its incoming connections were traversed.
    /// </summary>
    private bool IsReadyToEnqueue(string nodeId, int remainingPredecessors)
    {
        if (remainingPredecessors == 0)
            return true;

        var nodeDef = _definition.FindNode(nodeId);
        if (nodeDef is null || _nodes.GetPrototype(nodeDef.NodeType) is not IPartialExecutionNode partialNode)
            return false;

        var total = _definition.Connections.Count(c =>
            string.Equals(c.To, nodeId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c.FromPort, "resource", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(c.ToPort, "resource", StringComparison.OrdinalIgnoreCase));
        return partialNode.ShouldExecuteWithTraversedConnections(Math.Max(0, total - remainingPredecessors), total);
    }

    /// <summary>
    /// Decrements predecessor counts for successors of <paramref name="completedNodeId"/>
    /// and returns tasks that are now ready (remainingPredecessors == 0 and not yet visited/submitted).
    /// </summary>
    private async ValueTask<List<GraphTask>> GetReadySuccessorsAsync(string completedNodeId, IDictionary<string, object?> exits, CancellationToken ct)
    {
        var readyTasks = new List<GraphTask>();
        var outgoing = GetOutgoingConnections(completedNodeId).Where(c => StepExits.IsFollowed(c, exits)).ToList();

        foreach (var connection in outgoing)
        {
            var next = connection.To;

            UpsertTransition(_runIdStr, new RunStepTransitionSnapshot(FromStepId: connection.From, ToStepId: connection.To, Content: EmptyDict, Traversed: true, Timestamp: DateTimeOffset.UtcNow, FromPort: connection.FromPort, ToPort: connection.ToPort));
            await _notifyAsync(_runId, WorkflowRunStreamNotifications.ConnectionUpdated(_runId, connection.From, connection.FromPort, connection.To, connection.ToPort), false).ConfigureAwait(false);

            if (_context.VisitedNodes.Contains(next) || _submittedTasks.ContainsKey(next))
                continue;

            if (_remainingPredecessors.TryGetValue(next, out var count) && count > 0)
            {
                _remainingPredecessors[next] = count - 1;
            }

            if (_remainingPredecessors.TryGetValue(next, out var updated) && IsReadyToEnqueue(next, updated))
            {
                var task = await CreateAndEmitNodeTaskAsync(next, [], ct).ConfigureAwait(false);
                if (task is not null)
                    readyTasks.Add(task);
            }
        }

        return readyTasks;
    }

    /// <summary>
    /// Finds successors connected via the exit port from a control-flow result
    /// and returns tasks ready to run.
    /// </summary>
    private async ValueTask<List<GraphTask>> EnqueueNextNodesForControlFlowAsync(string nodeId, IDictionary<string, object?> outputs, CancellationToken ct)
    {
        var exitPort = "out";
        if (outputs.TryGetValue("__exit_port", out var portValue) && portValue is string port)
        {
            exitPort = port;
        }

        var connections = _definition
            .Connections.Where(c =>
                string.Equals(c.From, nodeId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.FromPort, exitPort, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c.FromPort, "resource", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c.ToPort, "resource", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        var readyTasks = new List<GraphTask>();

        foreach (var connection in connections)
        {
            var next = connection.To;

            UpsertTransition(_runIdStr, new RunStepTransitionSnapshot(FromStepId: connection.From, ToStepId: connection.To, Content: EmptyDict, Traversed: true, Timestamp: DateTimeOffset.UtcNow, FromPort: connection.FromPort, ToPort: connection.ToPort));
            await _notifyAsync(_runId, WorkflowRunStreamNotifications.ConnectionUpdated(_runId, connection.From, connection.FromPort, connection.To, connection.ToPort), false).ConfigureAwait(false);

            if (_context.VisitedNodes.Contains(next) || _submittedTasks.ContainsKey(next))
                continue;

            if (_remainingPredecessors.TryGetValue(next, out var count) && count > 0)
            {
                _remainingPredecessors[next] = count - 1;
            }

            if (!_remainingPredecessors.TryGetValue(next, out var updated) || IsReadyToEnqueue(next, updated))
            {
                var task = await CreateAndEmitNodeTaskAsync(next, [], ct).ConfigureAwait(false);
                if (task is not null)
                    readyTasks.Add(task);
            }
        }

        return readyTasks;
    }

    /// <summary>Executes a loop control-flow node inline: each item runs through the body of the loop.</summary>
    private async ValueTask<OperationResult<WorkflowRunResult>> ExecuteLoopAsync(string loopNodeId, ControlFlowResult controlResult, CancellationToken ct)
    {
        var state = _context.State;
        var items = controlResult.LoopItems ?? [];
        var statePrefix = controlResult.StateKeyPrefix ?? loopNodeId;
        var loopPort = controlResult.LoopPort;

        _logger.LogDebug("Executing loop for {NodeId} with {Count} items", loopNodeId, items.Count);

        var loopBodyNodes = _definition.Connections.Where(c => string.Equals(c.From, loopNodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.FromPort, loopPort, StringComparison.OrdinalIgnoreCase)).Select(c => c.To).ToList();

        if (loopBodyNodes.Count == 0)
        {
            _logger.LogWarning("No nodes connected to loop port '{Port}' of {NodeId}", loopPort, loopNodeId);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            state[$"{statePrefix}.current_item"] = item;
            state[$"{statePrefix}.current_index"] = i;
            state[$"{statePrefix}.total_count"] = items.Count;
            state[$"{statePrefix}.is_first"] = (i == 0);
            state[$"{statePrefix}.is_last"] = (i == items.Count - 1);

            _logger.LogDebug("Loop {NodeId}: iteration {Index}/{Total}", loopNodeId, i + 1, items.Count);

            foreach (var bodyNodeId in loopBodyNodes)
            {
                var iterResult = await ExecuteLoopBodyNodeAsync(bodyNodeId, i + 1, ct).ConfigureAwait(false);
                if (iterResult.IsFailure)
                    return iterResult;
            }
        }

        state[$"{statePrefix}.count"] = items.Count;
        state[$"{statePrefix}.completed"] = true;

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = items.Count,
            ["__control_flow_type"] = ControlFlowType.LoopComplete.ToString(),
            ["__exit_port"] = controlResult.CompletePort,
        };

        return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(_definition, outputs, _context.ExecutedNodes));
    }

    private async ValueTask<OperationResult<WorkflowRunResult>> ExecuteLoopBodyNodeAsync(string nodeId, int callNumber, CancellationToken ct)
    {
        var state = _context.State;

        var nodeDef = _definition.FindNode(nodeId);
        if (nodeDef is null)
        {
            var msg = $"Loop body node '{nodeId}' not found in workflow.";
            _logger.LogError(msg);
            return OperationResult<WorkflowRunResult>.Failure(msg);
        }

        if (_nodes.GetPrototype(nodeDef.NodeType) is not { } nodeKind)
        {
            var msg = $"No workflow node registered with name '{nodeDef.NodeType}'.";
            _logger.LogError(msg);
            return OperationResult<WorkflowRunResult>.Failure(msg);
        }

        if (nodeKind is ResourceNode)
            return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(_definition, new Dictionary<string, object?>(), _context.ExecutedNodes));

        var nodeImpl = _nodes.Create(nodeDef.NodeType); // a new node for this call (each iteration of the loop is a call)

        _logger.LogDebug("Executing loop body node {NodeId} ({Handler})", nodeDef.Id, nodeImpl.Name);

        UpsertStep(
            _runIdStr,
            nodeDef.Id,
            step =>
            {
                step.Name = nodeDef.Id;
                step.Status = RunStatus.Running;
                step.Inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        );
        await _notifyAsync(_runId, WorkflowRunStreamNotifications.NodeUpdated(_runId, nodeDef.Id, WorkflowNodeRunState.Running.ToString()), false).ConfigureAwait(false);
        await _emitNodeStartedAsync(_runId, _workflowId, nodeDef.Id, nodeDef.NodeType, state, ct).ConfigureAwait(false);

        var config = new NodeConfig(_definition, nodeDef, state);
        var runExecution = GetRun(_runIdStr)?.Execution;
        var services = new WorkflowRunServices(
            runId: _runId,
            checkpointAsync: async ct2 =>
            {
                if (GetRunControl(_runIdStr) is { } ctrl)
                    await ctrl.CheckpointAsync(ct2).ConfigureAwait(false);
            },
            publishStatus: msg => AppendStepLog(_runIdStr, nodeDef.Id, msg),
            updateTrace: updater =>
            {
                // Debug trace not supported in generic IRunRepository — no-op
            },
            logger: _logger,
            executionId: runExecution?.ExecutionId,
            streamId: runExecution?.StreamId,
            callNumber: callNumber
        );

        try
        {
            config.ResolveAll();
        }
        catch (Exception ex)
        {
            var msg = $"Failed to resolve inputs for loop body node '{nodeDef.Id}': {ex.Message}";
            _logger.LogError(ex, "Input resolution failed for loop body node {NodeId}", nodeDef.Id);
            return OperationResult<WorkflowRunResult>.Failure(msg);
        }

        IDictionary<string, object?>? handlerResult;
        try
        {
            handlerResult = await nodeImpl.ExecuteAsync(config, services, ct).ConfigureAwait(false);
        }
        catch (Agentivity.Infrastructure.Workflows.Interactions.WorkflowWaitException)
        {
            var msg = LoopBodyLimits.CannotWaitForAPerson(nodeDef.Id);
            _logger.LogError("Loop body node {NodeId} tried to suspend the run", nodeDef.Id);
            return OperationResult<WorkflowRunResult>.Failure(msg);
        }
        catch (Exception ex)
        {
            var msg = $"Execution of loop body node '{nodeDef.Id}' failed: {ex.Message}";
            _logger.LogError(ex, "Loop body node {NodeId} execution failed", nodeDef.Id);
            // It goes on by its Error exit when that exit leads somewhere (StepExits), otherwise the loop stops.
            handlerResult = StepExits.GoOnByError(_definition, nodeDef.Id, msg);
            if (handlerResult is null)
                return OperationResult<WorkflowRunResult>.Failure(msg);
        }

        if (handlerResult is null)
        {
            var msg = $"Loop body node '{nodeDef.Id}' returned no result.";
            return OperationResult<WorkflowRunResult>.Failure(msg);
        }

        foreach (var pair in handlerResult)
        {
            if (!pair.Key.StartsWith("__", StringComparison.Ordinal))
                state[$"{nodeId}.{pair.Key}"] = pair.Value;
        }

        var loopExits = handlerResult.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
        var loopOutputs = loopExits.Where(kvp => !kvp.Key.StartsWith("__", StringComparison.Ordinal)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        var loopStepFailed = StepExits.WentByError(loopExits);
        UpsertStep(
            _runIdStr,
            nodeDef.Id,
            step =>
            {
                step.Status = loopStepFailed ? RunStatus.Failed : RunStatus.Completed;
                step.Error = loopStepFailed ? loopOutputs[StepExits.Error]?.ToString() : step.Error;
                step.Outputs = loopOutputs;
            }
        );
        await _notifyAsync(_runId, WorkflowRunStreamNotifications.NodeUpdated(_runId, nodeDef.Id, WorkflowNodeRunState.Completed.ToString()), false).ConfigureAwait(false);
        await _emitNodeCompletedAsync(_runId, _workflowId, nodeDef.Id, nodeDef.NodeType, state, ct).ConfigureAwait(false);

        // Recurse into successors: by Out, or by the exit the step chose (Error, when it failed)
        var exitPort = StepExits.ChosenExit(loopExits) ?? "out";
        var successors = _definition.Connections.Where(c => string.Equals(c.From, nodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.FromPort, exitPort, StringComparison.OrdinalIgnoreCase)).Select(c => c.To).ToList();

        foreach (var successorId in successors)
        {
            // Skip loop boundary nodes (control-flow nodes)
            if (IsLoopBoundaryNode(successorId))
                continue;

            var successorResult = await ExecuteLoopBodyNodeAsync(successorId, callNumber, ct).ConfigureAwait(false);
            if (successorResult.IsFailure)
                return successorResult;
        }

        return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(_definition, loopOutputs, _context.ExecutedNodes));
    }

    private bool IsLoopBoundaryNode(string nodeId)
    {
        var nodeDef = _definition.FindNode(nodeId);
        if (nodeDef is null)
            return false;
        return _nodes.GetPrototype(nodeDef.NodeType) is IControlFlowNode;
    }
}
