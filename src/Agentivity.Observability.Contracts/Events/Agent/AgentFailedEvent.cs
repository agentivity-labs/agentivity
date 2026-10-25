namespace Agentivity.Observability.Contracts.Events.Agent;

public sealed record AgentFailedEvent(string AgentNodeId, string AgentMode, DateTime StartedAtUtc, DateTime FailedAtUtc, string ErrorMessage) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.AgentFailed;
}
