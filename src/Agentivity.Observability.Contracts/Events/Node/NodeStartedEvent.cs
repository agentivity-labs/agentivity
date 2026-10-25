namespace Agentivity.Observability.Contracts.Events.Node;

public sealed record NodeStartedEvent(string NodeId, string NodeType, DateTime StartedAtUtc) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.NodeStarted;
}
