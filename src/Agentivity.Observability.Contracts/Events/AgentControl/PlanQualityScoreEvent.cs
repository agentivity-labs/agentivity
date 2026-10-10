namespace Agentivity.Observability.Contracts.Events.AgentControl;

public sealed record PlanQualityScoreEvent(double Score, int TaskCount, DateTime OccurredAtUtc) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.PlanQualityScore;
}
