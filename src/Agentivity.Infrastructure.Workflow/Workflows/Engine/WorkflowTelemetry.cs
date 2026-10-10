using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Observability.Contracts.Events.Node;
using Agentivity.Observability.Contracts.Events.Run;

namespace Agentivity.Infrastructure.Workflows;

public sealed partial class WorkflowEngine
{
    private async ValueTask EmitRunStartedAsync(Guid runId, string workflowId, DateTime startedAtUtc, CancellationToken cancellationToken, RunContext? parentContext = null)
    {
        await _telemetryEmitter.EmitAsync(BuildCorrelationContext(runId, workflowId, parentContext: parentContext), new RunStartedEvent(workflowId, WorkflowVersion: null, startedAtUtc), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EmitRunCompletedAsync(Guid runId, string workflowId, DateTime startedAtUtc, CancellationToken cancellationToken)
    {
        var completedAtUtc = DateTime.UtcNow;
        await _telemetryEmitter
            .EmitAsync(BuildCorrelationContext(runId, workflowId), new RunCompletedEvent(workflowId, startedAtUtc, completedAtUtc, (long)Math.Max(0, (completedAtUtc - startedAtUtc).TotalMilliseconds), WorkflowRunState.Completed.ToString()), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask EmitNodeStartedAsync(Guid runId, string workflowId, string nodeId, string nodeType, Dictionary<string, object?> state, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        state[BuildNodeStartedAtStateKey(nodeId)] = startedAtUtc;
        await _telemetryEmitter.EmitAsync(BuildCorrelationContext(runId, workflowId, nodeId), new NodeStartedEvent(nodeId, nodeType, startedAtUtc), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EmitNodeCompletedAsync(Guid runId, string workflowId, string nodeId, string nodeType, Dictionary<string, object?> state, CancellationToken cancellationToken)
    {
        var startedAtUtc = TryGetNodeStartedAt(state, nodeId) ?? DateTime.UtcNow;
        var completedAtUtc = DateTime.UtcNow;
        await _telemetryEmitter.EmitAsync(BuildCorrelationContext(runId, workflowId, nodeId), new NodeCompletedEvent(nodeId, nodeType, startedAtUtc, completedAtUtc, (long)Math.Max(0, (completedAtUtc - startedAtUtc).TotalMilliseconds)), cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitFailureTelemetryAsync(Guid runId, string workflowId, DateTime runStartedAtUtc, string? nodeId, string? nodeType, Dictionary<string, object?>? state, string message)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            var nodeStartedAtUtc = state is null ? runStartedAtUtc : TryGetNodeStartedAt(state, nodeId!) ?? runStartedAtUtc;
            await _telemetryEmitter.EmitAsync(BuildCorrelationContext(runId, workflowId, nodeId), new NodeFailedEvent(nodeId!, nodeType ?? string.Empty, nodeStartedAtUtc, DateTime.UtcNow, message)).ConfigureAwait(false);
        }

        await _telemetryEmitter.EmitAsync(BuildCorrelationContext(runId, workflowId), new RunFailedEvent(workflowId, runStartedAtUtc, DateTime.UtcNow, message)).ConfigureAwait(false);
    }

    private static ExecutionCorrelationContext BuildCorrelationContext(Guid runId, string workflowId, string? nodeId = null, RunContext? parentContext = null)
    {
        // Propagate ExecutionId from parent when available (cross-engine observability).
        var traceId = parentContext?.Execution.ExecutionId ?? runId.ToString("N");
        var parentSpanId = nodeId is null ? parentContext?.RunId : workflowId;

        return new(RunId: runId, EntityId: workflowId, TraceId: traceId, SpanId: string.IsNullOrWhiteSpace(nodeId) ? workflowId : nodeId, ParentSpanId: parentSpanId, NodeExecutionId: nodeId);
    }

    private static string BuildNodeStartedAtStateKey(string nodeId) => $"__telemetry.node.started_at.{nodeId}";

    private static DateTime? TryGetNodeStartedAt(IReadOnlyDictionary<string, object?> state, string nodeId) => state.TryGetValue(BuildNodeStartedAtStateKey(nodeId), out var value) && value is DateTime dt ? dt : null;

    private static string? ValidateOutputTypes(Node node, IDictionary<string, object?> outputs, string nodeId)
    {
        if (node is not INodeMetadataProvider)
        {
            return null;
        }
        return null;
    }
}
