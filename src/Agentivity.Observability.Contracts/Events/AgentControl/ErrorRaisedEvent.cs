namespace Agentivity.Observability.Contracts.Events.AgentControl;

public sealed record ErrorRaisedEvent(string ScopeType, string ScopeId, DateTime OccurredAtUtc, string ErrorMessage, string? ErrorCode = null, bool IsTerminal = false) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.ErrorRaised;
}
