using Agentivity.Domain.Common;
using Agentivity.Domain.Run;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflows;

public sealed partial class WorkflowEngine
{
    private async ValueTask<OperationResult<WorkflowRunResult>> ExecuteControlFlowNodeAsync(
        Guid runId,
        WorkflowEntity definition,
        WorkflowNodeDefinition node,
        IControlFlowNode controlFlowNode,
        ControlFlowResult controlResult,
        Dictionary<string, object?> state,
        List<string> executedNodes,
        CancellationToken cancellationToken
    )
    {
        // Store any outputs from the control flow evaluation
        if (controlResult.Outputs is not null)
        {
            foreach (var pair in controlResult.Outputs)
            {
                state[$"{node.Id}.{pair.Key}"] = pair.Value;
            }
        }

        switch (controlResult.Type)
        {
            case ControlFlowType.Loop:
                return await ExecuteLoopAsync(runId, definition, node, controlResult, state, executedNodes, cancellationToken).ConfigureAwait(false);

            case ControlFlowType.LoopComplete:
            case ControlFlowType.Continue:
            case ControlFlowType.Branch:
                var resultOutputs = new Dictionary<string, object?>(controlResult.Outputs ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase) { ["__control_flow_type"] = controlResult.Type.ToString(), ["__exit_port"] = controlResult.ExitPort ?? controlResult.CompletePort };
                return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, resultOutputs, executedNodes));

            case ControlFlowType.Skip:
                _logger.LogDebug("Skipping downstream nodes for {NodeId}", node.Id);
                var skipOutputs = new Dictionary<string, object?>(controlResult.Outputs ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase);
                return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, skipOutputs, executedNodes));

            default:
                var message = $"Unknown control flow type: {controlResult.Type}";
                FailRun(runId, definition.Id, node.Id, message);
                return OperationResult<WorkflowRunResult>.Failure(message);
        }
    }

    private async ValueTask<OperationResult<WorkflowRunResult>> ExecuteControlFlowNodeAsync(
        Guid runId,
        WorkflowEntity definition,
        WorkflowNodeDefinition node,
        IControlFlowNode controlFlowNode,
        NodeConfig config,
        WorkflowRunServices services,
        Dictionary<string, object?> state,
        List<string> executedNodes,
        CancellationToken cancellationToken
    )
    {
        ControlFlowResult controlResult;
        try
        {
            controlResult = await controlFlowNode.EvaluateAsync(config, services, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = $"Control flow evaluation failed for node '{node.Id}': {ex.Message}";
            FailRun(runId, definition.Id, node.Id, message, exception: ex);
            return OperationResult<WorkflowRunResult>.Failure(message);
        }

        return await ExecuteControlFlowNodeAsync(runId, definition, node, controlFlowNode, controlResult, state, executedNodes, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OperationResult<WorkflowRunResult>> ExecuteLoopAsync(Guid runId, WorkflowEntity definition, WorkflowNodeDefinition loopNode, ControlFlowResult controlResult, Dictionary<string, object?> state, List<string> executedNodes, CancellationToken cancellationToken)
    {
        var items = controlResult.LoopItems ?? [];
        var statePrefix = controlResult.StateKeyPrefix ?? loopNode.Id;
        var loopPort = controlResult.LoopPort;

        _logger.LogDebug("Executing loop for {NodeId} with {Count} items", loopNode.Id, items.Count);

        var loopBodyNodes = GetNodesConnectedToPort(definition, loopNode.Id, loopPort);

        if (loopBodyNodes.Count == 0)
        {
            _logger.LogWarning("No nodes connected to loop port '{Port}' of {NodeId}", loopPort, loopNode.Id);
        }

        var allIterationResults = new List<IDictionary<string, object?>>();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            state[$"{statePrefix}.current_item"] = item;
            state[$"{statePrefix}.current_index"] = i;
            state[$"{statePrefix}.total_count"] = items.Count;
            state[$"{statePrefix}.is_first"] = (i == 0);
            state[$"{statePrefix}.is_last"] = (i == items.Count - 1);

            _logger.LogDebug("Loop {NodeId}: iteration {Index}/{Total}", loopNode.Id, i + 1, items.Count);

            foreach (var bodyNodeId in loopBodyNodes)
            {
                var iterationResult = await ExecuteLoopBodyNodeLegacyAsync(runId, definition, bodyNodeId, state, executedNodes, i + 1, cancellationToken).ConfigureAwait(false);

                if (iterationResult.IsFailure)
                {
                    return iterationResult;
                }
            }

            var iterationState = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["index"] = i, ["item"] = item };
            allIterationResults.Add(iterationState);
        }

        state[$"{statePrefix}.results"] = System.Text.Json.JsonSerializer.Serialize(allIterationResults);
        state[$"{statePrefix}.count"] = items.Count;
        state[$"{statePrefix}.completed"] = true;

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["results"] = allIterationResults,
            ["count"] = items.Count,
            ["__control_flow_type"] = ControlFlowType.LoopComplete.ToString(),
            ["__exit_port"] = controlResult.CompletePort,
        };

        return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, outputs, executedNodes));
    }

    private async ValueTask<OperationResult<WorkflowRunResult>> ExecuteLoopBodyNodeLegacyAsync(Guid runId, WorkflowEntity definition, string nodeId, Dictionary<string, object?> state, List<string> executedNodes, int callNumber, CancellationToken cancellationToken)
    {
        var node = definition.FindNode(nodeId);
        if (node is null)
        {
            var message = $"Loop body node '{nodeId}' not found in workflow.";
            FailRun(runId, definition.Id, nodeId, message);
            return OperationResult<WorkflowRunResult>.Failure(message);
        }

        if (_nodeFactory.GetPrototype(node.NodeType) is not { } nodeKind)
        {
            var message = $"No workflow node registered with name '{node.NodeType}'.";
            FailRun(runId, definition.Id, nodeId, message);
            return OperationResult<WorkflowRunResult>.Failure(message);
        }

        if (nodeKind is ResourceNode)
        {
            return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, new Dictionary<string, object?>(), executedNodes));
        }

        var WorkflowNodeDefinition = _nodeFactory.Create(node.NodeType); // a new node for this call

        _logger.LogDebug("Executing loop body node {NodeId} ({Handler})", node.Id, WorkflowNodeDefinition.Name);

        var runIdStr = runId.ToString("D");
        var config = new NodeConfig(definition, node, state);
        var runExecution = GetRun(runIdStr)?.Execution;
        var services = new WorkflowRunServices(
            runId: runId,
            checkpointAsync: async ct2 =>
            {
                if (GetRunControl(runIdStr) is { } ctrl)
                    await ctrl.CheckpointAsync(ct2).ConfigureAwait(false);
            },
            publishStatus: msg => AppendStepLog(runIdStr, node.Id, msg),
            updateTrace: updater =>
            { /* no-op: trace not supported in IRunRepository */
            },
            telemetryEmitter: _telemetryEmitter,
            correlationContext: BuildCorrelationContext(runId, definition.Id, node.Id),
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
            var message = $"Failed to resolve inputs for loop body node '{node.Id}': {ex.Message}";
            FailRun(runId, definition.Id, node.Id, message, exception: ex);
            return OperationResult<WorkflowRunResult>.Failure(message);
        }

        UpsertStep(
            runIdStr,
            node.Id,
            step =>
            {
                step.Name = node.Id;
                step.Status = RunStatus.Running;
                step.Inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        );
        Notify(runId, WorkflowRunStreamNotifications.NodeUpdated(runId, node.Id, WorkflowNodeRunState.Running.ToString()));
        await EmitNodeStartedAsync(runId, definition.Id, node.Id, node.NodeType, state, cancellationToken).ConfigureAwait(false);

        IDictionary<string, object?>? handlerResult;
        try
        {
            handlerResult = await WorkflowNodeDefinition.ExecuteAsync(config, services, cancellationToken).ConfigureAwait(false);
        }
        catch (Agentivity.Infrastructure.Workflows.Interactions.WorkflowSuspendedException ex)
        {
            var message = LoopBodyLimits.CannotWaitForAPerson(node.Id);
            FailRun(runId, definition.Id, node.Id, message, exception: ex);
            return OperationResult<WorkflowRunResult>.Failure(message);
        }
        catch (Exception ex)
        {
            var message = $"Execution of loop body node '{node.Id}' failed: {ex.Message}";
            FailRun(runId, definition.Id, node.Id, message, exception: ex);
            return OperationResult<WorkflowRunResult>.Failure(message);
        }

        if (handlerResult is null)
        {
            var message = $"Loop body node '{node.Id}' returned no result.";
            FailRun(runId, definition.Id, node.Id, message);
            return OperationResult<WorkflowRunResult>.Failure(message);
        }

        foreach (var pair in handlerResult)
        {
            state[$"{nodeId}.{pair.Key}"] = pair.Value;
        }

        var outputs = handlerResult.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
        UpsertStep(
            runIdStr,
            node.Id,
            step =>
            {
                step.Status = RunStatus.Completed;
                step.Outputs = outputs;
            }
        );
        Notify(runId, WorkflowRunStreamNotifications.NodeUpdated(runId, node.Id, WorkflowNodeRunState.Completed.ToString()));
        await EmitNodeCompletedAsync(runId, definition.Id, node.Id, node.NodeType, state, cancellationToken).ConfigureAwait(false);

        var successors = GetNodesConnectedToPort(definition, nodeId, "out");
        foreach (var successorId in successors)
        {
            if (IsLoopBoundaryNode(definition, successorId))
            {
                continue;
            }

            var successorResult = await ExecuteLoopBodyNodeLegacyAsync(runId, definition, successorId, state, executedNodes, callNumber, cancellationToken).ConfigureAwait(false);

            if (successorResult.IsFailure)
            {
                return successorResult;
            }
        }

        return OperationResult<WorkflowRunResult>.Success(new WorkflowRunResult(definition, outputs, executedNodes));
    }

    private void EnqueueNextNodesForControlFlow(WorkflowEntity definition, string nodeId, IDictionary<string, object?> outputs, Dictionary<string, int> remainingPredecessors, Queue<string> pending, Guid runId)
    {
        var exitPort = "out";
        if (outputs.TryGetValue("__exit_port", out var portValue) && portValue is string port)
        {
            exitPort = port;
        }

        var connections = definition
            .Connections.Where(c =>
                string.Equals(c.From, nodeId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.FromPort, exitPort, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c.FromPort, "resource", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c.ToPort, "resource", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        foreach (var connection in connections)
        {
            var next = connection.To;

            AddTransition(runId.ToString("D"), new Domain.Run.RunStepTransitionSnapshot(FromStepId: connection.From, ToStepId: connection.To, Content: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), Traversed: true, Timestamp: DateTimeOffset.UtcNow));

            Notify(runId, WorkflowRunStreamNotifications.ConnectionUpdated(runId, connection.From, connection.FromPort, connection.To, connection.ToPort));

            if (pending.Contains(next))
            {
                continue;
            }

            if (remainingPredecessors.TryGetValue(next, out var count) && count > 0)
            {
                remainingPredecessors[next] = count - 1;
            }

            if (ShouldEnqueueNode(definition, next, runId))
            {
                pending.Enqueue(next);
            }
        }
    }

    private static List<string> GetNodesConnectedToPort(WorkflowEntity definition, string nodeId, string port) =>
        definition.Connections.Where(c => string.Equals(c.From, nodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.FromPort, port, StringComparison.OrdinalIgnoreCase)).Select(c => c.To).ToList();

    private bool IsLoopBoundaryNode(WorkflowEntity definition, string nodeId)
    {
        return _nodeFactory.GetPrototype(definition.FindNode(nodeId)?.NodeType ?? "") is IControlFlowNode;
    }
}
