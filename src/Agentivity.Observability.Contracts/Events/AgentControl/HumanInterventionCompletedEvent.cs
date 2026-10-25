namespace Agentivity.Observability.Contracts.Events.AgentControl;

public sealed record HumanInterventionCompletedEvent(string QuestionId, DateTime RequestedAtUtc, DateTime CompletedAtUtc, long DurationMs, string Outcome, string? ResponseSummary = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.HumanInterventionCompleted;
}
