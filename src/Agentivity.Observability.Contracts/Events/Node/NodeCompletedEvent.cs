namespace Agentivity.Observability.Contracts.Events.Node;

public sealed record NodeCompletedEvent(string NodeId, string NodeType, DateTime StartedAtUtc, DateTime CompletedAtUtc, long DurationMs) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.NodeCompleted;
}
