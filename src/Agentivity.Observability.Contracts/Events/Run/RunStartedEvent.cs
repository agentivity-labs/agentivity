namespace Agentivity.Observability.Contracts.Events.Run;

public sealed record RunStartedEvent(string WorkflowId, string? WorkflowVersion, DateTime StartedAtUtc, string? Environment = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.RunStarted;
}
