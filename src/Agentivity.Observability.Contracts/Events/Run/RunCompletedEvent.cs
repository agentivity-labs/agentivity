namespace Agentivity.Observability.Contracts.Events.Run;

public sealed record RunCompletedEvent(string WorkflowId, DateTime StartedAtUtc, DateTime CompletedAtUtc, long DurationMs, string Status) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.RunCompleted;
}
