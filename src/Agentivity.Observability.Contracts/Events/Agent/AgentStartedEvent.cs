namespace Agentivity.Observability.Contracts.Events.Agent;

public sealed record AgentStartedEvent(string AgentNodeId, string AgentMode, DateTime StartedAtUtc) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.AgentStarted;
}
