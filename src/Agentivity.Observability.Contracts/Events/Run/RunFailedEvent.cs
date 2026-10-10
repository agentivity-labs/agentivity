namespace Agentivity.Observability.Contracts.Events.Run;

public sealed record RunFailedEvent(string WorkflowId, DateTime StartedAtUtc, DateTime FailedAtUtc, string ErrorMessage, string? ErrorCode = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.RunFailed;
}
