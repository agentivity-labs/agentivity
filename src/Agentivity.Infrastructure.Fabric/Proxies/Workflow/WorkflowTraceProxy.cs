using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts.Correlation;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Workflow implementation of <see cref="IAgentTraceProxy"/>.
/// Writes execution trace events into the node's <c>DebugTraceAccumulator</c>.
/// </summary>
public sealed class WorkflowTraceProxy(NodeConfig config, WorkflowRunServices services) : IAgentTraceProxy
{
    public void Initialize(AgentTraceMetadata metadata) => services.UpdateTrace(accumulator => AgentExecutionTraceRootHelper.EnsureExecutionRoot(accumulator, metadata, 0));

    public void AppendEvent(AgentTraceMetadata metadata, AgentTraceEvent evt)
    {
        services.UpdateTrace(accumulator =>
        {
            AgentExecutionTraceRootHelper.EnsureExecutionRoot(accumulator, metadata, evt.Iteration);
            accumulator.Append(evt.ToDebugTraceItem());
        });
    }

    public void UpsertContextLayersSnapshot(AgentTraceMetadata metadata, int iteration, string snapshot)
    {
        services.UpdateTrace(accumulator =>
        {
            AgentExecutionTraceRootHelper.EnsureExecutionRoot(accumulator, metadata, iteration);

            if (accumulator.Root is null)
            {
                return;
            }

            var existing = accumulator.Root.Children.FirstOrDefault(child => string.Equals(child.Kind, "context_layers_snapshot", StringComparison.Ordinal));
            if (existing is null)
            {
                accumulator.Root.Children.Add(new DebugTraceNode("context_layers_snapshot", "Injected context layers", snapshot, DebugTraceStatus.Info));
                return;
            }

            existing.Name = "Injected context layers";
            existing.Message = snapshot;
            existing.Status = DebugTraceStatus.Info;
            existing.Fields.Clear();
            existing.Children.Clear();
        });
    }

    /// <summary>
    /// Builds the correlation context for agent-control telemetry events (custom widget calls,
    /// human-intervention requested/completed) — shared by <see cref="WorkflowHilProxy"/> and
    /// <see cref="WorkflowAgentLlmProxy"/>.
    /// </summary>
    internal static ExecutionCorrelationContext BuildAgentControlCorrelationContext(NodeConfig config, WorkflowRunServices services, string stepId, string spanId)
    {
        var parent = services.CorrelationContext;
        var agentRunId = config.State.TryGetValue(AgentRuntimeKeys.AgentRunIdStateKey, out var rawAgentRunId) ? rawAgentRunId as string : null;

        return new ExecutionCorrelationContext(
            RunId: services.RunId,
            EntityId: parent?.EntityId ?? config.Definition.Id,
            TraceId: parent?.TraceId ?? services.RunId.ToString("N"),
            SpanId: spanId,
            ParentSpanId: agentRunId ?? parent?.SpanId,
            NodeRunId: agentRunId,
            NodeExecutionId: config.Node.Id,
            StepId: stepId
        );
    }
}
