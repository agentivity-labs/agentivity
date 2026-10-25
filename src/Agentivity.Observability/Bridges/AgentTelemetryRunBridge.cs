using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Observability.Bridges;

/// <summary>
/// Implémente <see cref="IAgentTelemetrySink"/> et traduit chaque <see cref="AgentTelemetryEnvelope"/>
/// en <see cref="RunEvent"/> publié sur l'<see cref="IRunEventBus"/> unifié.
/// </summary>
public sealed class AgentTelemetryRunBridge(IRunEventBus bus) : IAgentTelemetrySink
{
    public ValueTask WriteAsync(AgentTelemetryEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var runEvent = new RunEvent(
            RunId: envelope.Correlation.NodeRunId ?? envelope.Correlation.RunId.ToString("D"),
            DefinitionId: envelope.Correlation.EntityId,
            RunKind: RunKind.Agent,
            Kind: MapKind(envelope.Kind),
            EventName: envelope.EventName,
            OccurredAtUtc: new DateTimeOffset(envelope.OccurredAtUtc, TimeSpan.Zero),
            Payload: envelope.Payload
        );

        bus.Publish(runEvent);

        return ValueTask.CompletedTask;
    }

    private static RunEventKind MapKind(AgentTelemetryEventKind k) =>
        k switch
        {
            AgentTelemetryEventKind.RunStarted => RunEventKind.run_started,
            AgentTelemetryEventKind.RunCompleted => RunEventKind.run_completed,
            AgentTelemetryEventKind.RunFailed => RunEventKind.run_failed,
            AgentTelemetryEventKind.NodeStarted => RunEventKind.node_started,
            AgentTelemetryEventKind.NodeCompleted => RunEventKind.node_completed,
            AgentTelemetryEventKind.NodeFailed => RunEventKind.node_failed,
            AgentTelemetryEventKind.AgentStarted => RunEventKind.agent_started,
            AgentTelemetryEventKind.AgentCompleted => RunEventKind.agent_completed,
            AgentTelemetryEventKind.AgentFailed => RunEventKind.agent_failed,
            AgentTelemetryEventKind.LlmCallStarted => RunEventKind.llm_call_started,
            AgentTelemetryEventKind.LlmCallCompleted => RunEventKind.llm_call_completed,
            AgentTelemetryEventKind.ToolCallStarted => RunEventKind.tool_call_started,
            AgentTelemetryEventKind.ToolCallCompleted => RunEventKind.tool_call_completed,
            AgentTelemetryEventKind.ReplanTriggered => RunEventKind.replan_triggered,
            AgentTelemetryEventKind.HumanInterventionRequested => RunEventKind.hil_gate_reached,
            AgentTelemetryEventKind.HumanInterventionCompleted => RunEventKind.hil_approved,
            AgentTelemetryEventKind.ErrorRaised => RunEventKind.error_raised,
            AgentTelemetryEventKind.PlanCreated => RunEventKind.plan_created,
            AgentTelemetryEventKind.CustomWidgetInvoked => RunEventKind.custom_widget_invoked,
            _ => RunEventKind.message_sent,
        };
}
