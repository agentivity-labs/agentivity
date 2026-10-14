using Agentivity.Domain.Common;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Graph;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Workflows;

public sealed partial class WorkflowEngine
{
    private async ValueTask<OperationResult<WorkflowRunResult>> ExecuteInternalAsync(Guid runId, string workflowId, IDictionary<string, object?>? input, CancellationToken cancellationToken, RunContext? parentContext = null)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return OperationResult<WorkflowRunResult>.Failure("Workflow name is required");
        }

        input ??= new Dictionary<string, object?>();

        var definition = await _workflowService.GetByIdAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return OperationResult<WorkflowRunResult>.Failure($"Workflow '{workflowId}' not found.");
        }

        var state = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in input)
        {
            state[key] = value;
        }

        var predecessors = BuildPredecessorMap(definition);

        var remainingPredecessors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in definition.Nodes)
        {
            remainingPredecessors[node.Id] = predecessors.TryGetValue(node.Id, out var preds) ? preds.Count : 0;
        }

        var runControl = new RunControl(cancellationToken);
        var runStartedAtUtc = DateTime.UtcNow;
        var executionId = parentContext?.Execution.ExecutionId ?? runId.ToString("N");
        var runIdStr = runId.ToString("D");

        // parentContext is only supplied when this workflow runs nested inside a team/agent — a
        // root run needs its own context so EntityId (workflowId) is always captured on RunRecord.
        // RunId is forced to the caller-supplied runId: StartNew generates a fresh random Guid,
        // which would otherwise leave the registered context's RunId out of sync with the key
        // it's registered under in IRunRepository (breaking resume/lookups by runId).
        var candidate = parentContext ?? RunContext.StartNew(Agentivity.Observability.Contracts.Events.RunKind.Workflow, workflowId, executionId);
        if (parentContext is null)
            candidate.RunId = runIdStr;
        await RunContext.FindOrRegister(_runRepository, candidate, cancellationToken).ConfigureAwait(false);
        _runWorkflowIds[runIdStr] = workflowId;

        // Resource nodes = nodes that appear as From in a resource-port connection.
        var resourceNodeIds = new HashSet<string>(definition.Connections.Where(c => string.Equals(c.FromPort, "resource", StringComparison.OrdinalIgnoreCase) || string.Equals(c.ToPort, "resource", StringComparison.OrdinalIgnoreCase)).Select(c => c.From), StringComparer.OrdinalIgnoreCase);

        // Initialize steps for all nodes, marking resource nodes with SubType="resource".
        foreach (var nodeDef in definition.Nodes)
        {
            var isResource = resourceNodeIds.Contains(nodeDef.Id);
            UpsertStep(
                runIdStr,
                nodeDef.Id,
                step =>
                {
                    step.Name = nodeDef.Id;
                    step.Status = RunStatus.Pending;
                    step.SubType = isResource ? "resource" : null;
                }
            );
        }

        // Initialize handoffs for all connections (traversed=false), including port info
        // so the status endpoint can identify resource links and animate them correctly.
        foreach (var connection in definition.Connections)
        {
            UpsertTransition(
                runIdStr,
                new Domain.Run.RunStepTransitionSnapshot(FromStepId: connection.From, ToStepId: connection.To, Content: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), Traversed: false, Timestamp: DateTimeOffset.UtcNow, FromPort: connection.FromPort, ToPort: connection.ToPort)
            );
        }

        SetRunControl(runIdStr, runControl);

        var agUiBundles = _agUiBundleRegistry is not null ? AgUiCapabilityResolver.ResolveActiveBundles(definition, _agUiBundleRegistry) : new Dictionary<string, IReadOnlyList<string>>();

        Notify(runId, WorkflowRunStreamNotifications.RunStartedWithAgUi(runId, agUiBundles));
        await EmitRunStartedAsync(runId, workflowId, runStartedAtUtc, cancellationToken, parentContext).ConfigureAwait(false);

        _logger.LogInformation("Workflow run {WorkflowRunId} started for workflow {WorkflowId}", runId, definition.Id);

        var workflowContext = new WorkflowGraphContext(state);
        var executionStore = new WorkflowGraphCheckpointPort(_runRepository);
        var orchestrator = new WorkflowGraphOrchestrator(
            runId: runId,
            definition: definition,
            nodes: _nodeFactory,
            remainingPredecessors: remainingPredecessors,
            context: workflowContext,
            runRepository: _runRepository,
            telemetryEmitter: _telemetryEmitter,
            logger: _logger,
            workflowId: workflowId,
            runStartedAtUtc: runStartedAtUtc,
            parentContext: parentContext,
            notifyAsync: (runId2, notification, terminal) =>
            {
                Notify(runId2, notification, terminal);
                return ValueTask.CompletedTask;
            },
            emitNodeStartedAsync: (rid, wid, nodeId, nodeType, state2, ct) => EmitNodeStartedAsync(rid, wid, nodeId, nodeType, state2, ct),
            emitNodeCompletedAsync: (rid, wid, nodeId, nodeType, state2, ct) => EmitNodeCompletedAsync(rid, wid, nodeId, nodeType, state2, ct),
            resourceMonitor: _resourceMonitor
        );
        executionStore.Orchestrator = orchestrator;

        try
        {
            GraphSession session;
            try
            {
                session = await _graphHost.SubmitAsync(orchestrator, GraphProfile.Persistent(executionStore), runControl.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runControl.IsCancelled)
            {
                SetRunState(runIdStr, RunStatus.Cancelled);
                return OperationResult<WorkflowRunResult>.Failure("Workflow was cancelled.");
            }

            GraphExecutionResult graphResult;
            try
            {
                graphResult = await _graphHost.AwaitCompletionAsync(session.GraphId, runControl.Token).ConfigureAwait(false);
            }
            catch (WorkflowWaitException)
            {
                // HIL suspension: WorkflowExecutionStore.SaveAsync already persisted the run state
                // when GraphHost caught the IGraphSignalException. Update the registry and notify.
                var hil = orchestrator.WorkflowContext.HilSuspension;
                if (hil is not null)
                {
                    SetRunState(runIdStr, RunStatus.WaitingForInput);
                    if (!string.IsNullOrWhiteSpace(hil.NodeId))
                    {
                        UpsertStep(
                            runIdStr,
                            hil.NodeId,
                            step =>
                            {
                                step.Status = RunStatus.WaitingForInput;
                                if (hil.PartialOutputs is not null)
                                    step.Outputs = new Dictionary<string, object?>(hil.PartialOutputs, StringComparer.OrdinalIgnoreCase);
                            }
                        );
                    }
                    Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "waiting_for_input", state: WorkflowRunState.WaitingForInput.ToString()));
                }
                _logger.LogInformation("Workflow {RunId} suspended at node '{NodeId}'", runId, hil?.NodeId ?? "unknown");
                return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, state, orchestrator.ExecutedNodes.ToList()) { Suspended = true, InteractionRequestId = hil?.InteractionRequestId, WaitingForToken = hil?.WaitingForToken });
            }
            catch (OperationCanceledException) when (runControl.IsCancelled)
            {
                SetRunState(runIdStr, RunStatus.Cancelled);
                return OperationResult<WorkflowRunResult>.Failure("Workflow was cancelled.");
            }

            if (graphResult.OverallStatus == GraphTaskStatus.Failed)
            {
                var err = graphResult.FinalSnapshot.Tasks.FirstOrDefault(t => t.State.RuntimeStatus == GraphTaskRuntimeStatus.Failed)?.State.Error ?? "Workflow failed.";
                SetRunState(runIdStr, RunStatus.Failed, err);
                return OperationResult<WorkflowRunResult>.Failure(err);
            }

            if (orchestrator.Failure is { } controlFlowFailure)
            {
                SetRunState(runIdStr, RunStatus.Failed, controlFlowFailure);
                return OperationResult<WorkflowRunResult>.Failure(controlFlowFailure);
            }

            // Completed
            var finalState = state.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
            KeepWhatItProduced(runIdStr, finalState, orchestrator.ExecutedNodes, parentContext);
            SetRunState(runIdStr, RunStatus.Completed, completedAt: DateTimeOffset.UtcNow);

            _logger.LogInformation("Workflow run {WorkflowRunId} completed for workflow {WorkflowId} after {DurationMs} ms", runId, definition.Id, (long)Math.Max(0, (DateTime.UtcNow - runStartedAtUtc).TotalMilliseconds));

            Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "completed", state: WorkflowRunState.Completed.ToString()), terminal: true);
            await EmitRunCompletedAsync(runId, workflowId, runStartedAtUtc, cancellationToken).ConfigureAwait(false);

            return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, finalState, orchestrator.ExecutedNodes.ToList()));
        }
        catch (OperationCanceledException) when (runControl.IsCancelled)
        {
            SetRunState(runIdStr, RunStatus.Cancelled);

            Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "cancelled", state: WorkflowRunState.Cancelled.ToString()), terminal: true);

            return OperationResult<WorkflowRunResult>.Failure("Workflow was cancelled.");
        }
        finally
        {
            // RunRepository.RemoveRun disposes the control; we also dispose here for safety
            runControl.Dispose();
        }
    }

    private async ValueTask<OperationResult<WorkflowRunResult>> ResumeExecutionLoopAsync(
        Guid runId,
        WorkflowEntity definition,
        Dictionary<string, object?> state,
        List<string> executedNodes,
        HashSet<string> visitedNodes,
        Queue<string> pending,
        Dictionary<string, int> remainingPredecessors,
        RunControl runControl,
        string resumeNodeId,
        CancellationToken cancellationToken,
        RunContext? parentContext = null
    )
    {
        var runIdStr = runId.ToString("D");

        // Remove the suspended node from visited so it can be re-executed
        visitedNodes.Remove(resumeNodeId);

        // The resumeNodeId goes first, then any other pending nodes
        var initialNodeIds = new List<string> { resumeNodeId };
        while (pending.Count > 0)
        {
            var next = pending.Dequeue();
            if (!string.Equals(next, resumeNodeId, StringComparison.OrdinalIgnoreCase))
                initialNodeIds.Add(next);
        }

        var workflowContext = new WorkflowGraphContext(state);

        // Restore visited/executed state into context
        foreach (var visited in visitedNodes)
            workflowContext.VisitedNodes.Add(visited);
        workflowContext.ExecutedNodes.AddRange(executedNodes);

        var workflowId = definition.Id;
        var runStartedAtUtc = GetRun(runIdStr)?.CreatedAt.DateTime ?? DateTime.UtcNow;

        var executionStore = new WorkflowGraphCheckpointPort(_runRepository);
        var orchestrator = new WorkflowGraphOrchestrator(
            runId: runId,
            definition: definition,
            nodes: _nodeFactory,
            remainingPredecessors: remainingPredecessors,
            context: workflowContext,
            runRepository: _runRepository,
            telemetryEmitter: _telemetryEmitter,
            logger: _logger,
            workflowId: workflowId,
            runStartedAtUtc: runStartedAtUtc,
            parentContext: parentContext,
            notifyAsync: (runId2, notification, terminal) =>
            {
                Notify(runId2, notification, terminal);
                return ValueTask.CompletedTask;
            },
            emitNodeStartedAsync: (_, _, _, _, _, _) => ValueTask.CompletedTask,
            emitNodeCompletedAsync: (_, _, _, _, _, _) => ValueTask.CompletedTask,
            initialNodeIds: initialNodeIds
        );
        executionStore.Orchestrator = orchestrator;

        GraphSession session;
        try
        {
            session = await _graphHost.SubmitAsync(orchestrator, GraphProfile.Persistent(executionStore), runControl.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runControl.IsCancelled)
        {
            SetRunState(runIdStr, RunStatus.Cancelled);
            Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "cancelled", state: WorkflowRunState.Cancelled.ToString()), terminal: true);
            return OperationResult<WorkflowRunResult>.Failure("Workflow was cancelled.");
        }

        GraphExecutionResult graphResult;
        try
        {
            graphResult = await _graphHost.AwaitCompletionAsync(session.GraphId, runControl.Token).ConfigureAwait(false);
        }
        catch (WorkflowWaitException)
        {
            // HIL suspension again: WorkflowExecutionStore.SaveAsync already persisted the run state.
            var hil = orchestrator.WorkflowContext.HilSuspension;
            if (hil is not null)
            {
                SetRunState(runIdStr, RunStatus.WaitingForInput);
                if (!string.IsNullOrWhiteSpace(hil.NodeId))
                {
                    UpsertStep(
                        runIdStr,
                        hil.NodeId,
                        step =>
                        {
                            step.Status = RunStatus.WaitingForInput;
                            if (hil.PartialOutputs is not null)
                                step.Outputs = new Dictionary<string, object?>(hil.PartialOutputs, StringComparer.OrdinalIgnoreCase);
                        }
                    );
                }
                Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "waiting_for_input", state: WorkflowRunState.WaitingForInput.ToString()));
            }
            _logger.LogInformation("Workflow {RunId} suspended at node '{NodeId}'", runId, hil?.NodeId ?? "unknown");
            return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, state, orchestrator.ExecutedNodes.ToList()) { Suspended = true, InteractionRequestId = hil?.InteractionRequestId, WaitingForToken = hil?.WaitingForToken });
        }
        catch (OperationCanceledException) when (runControl.IsCancelled)
        {
            SetRunState(runIdStr, RunStatus.Cancelled);
            Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "cancelled", state: WorkflowRunState.Cancelled.ToString()), terminal: true);
            return OperationResult<WorkflowRunResult>.Failure("Workflow was cancelled.");
        }

        if (graphResult.OverallStatus == GraphTaskStatus.Failed)
        {
            var err = graphResult.FinalSnapshot.Tasks.FirstOrDefault(t => t.State.RuntimeStatus == GraphTaskRuntimeStatus.Failed)?.State.Error ?? "Workflow failed.";
            SetRunState(runIdStr, RunStatus.Failed, err);
            return OperationResult<WorkflowRunResult>.Failure(err);
        }

        if (orchestrator.Failure is { } controlFlowFailure)
        {
            SetRunState(runIdStr, RunStatus.Failed, controlFlowFailure);
            return OperationResult<WorkflowRunResult>.Failure(controlFlowFailure);
        }

        var finalState = state.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
        KeepWhatItProduced(runIdStr, finalState, orchestrator.ExecutedNodes, parentContext);
        SetRunState(runIdStr, RunStatus.Completed, completedAt: DateTimeOffset.UtcNow);
        Notify(runId, WorkflowRunStreamNotifications.RunUpdated(runId, reason: "completed", state: WorkflowRunState.Completed.ToString()), terminal: true);

        return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, finalState, orchestrator.ExecutedNodes.ToList()));
    }
}
