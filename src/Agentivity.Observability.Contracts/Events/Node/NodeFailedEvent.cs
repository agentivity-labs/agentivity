namespace Agentivity.Observability.Contracts.Events.Node;

public sealed record NodeFailedEvent(string NodeId, string NodeType, DateTime StartedAtUtc, DateTime FailedAtUtc, string ErrorMessage) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.NodeFailed;
}
