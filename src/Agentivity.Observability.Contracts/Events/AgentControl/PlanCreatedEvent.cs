namespace Agentivity.Observability.Contracts.Events.AgentControl;

/// <summary>
/// Emitted when the P&amp;E planner produces a new execution plan, optionally carrying
/// the quality assessment score so downstream sinks can surface plan.quality.score.
/// </summary>
public sealed record PlanCreatedEvent(int Attempt, int TaskCount, DateTime OccurredAtUtc, double? QualityScore = null, double? QualityThreshold = null, bool? ThresholdReached = null, IReadOnlyList<string>? TaskIds = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.PlanCreated;
}
