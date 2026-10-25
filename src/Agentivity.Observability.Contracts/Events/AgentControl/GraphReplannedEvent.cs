namespace Agentivity.Observability.Contracts.Events.AgentControl;

/// <summary>
/// Emitted when the P&amp;E join actor triggers a graph-level replanning cycle
/// (distinct from ReplanTriggered which covers tool-failure replanning).
/// </summary>
public sealed record GraphReplannedEvent(string Reason, int GraphRevisionCount, DateTime OccurredAtUtc, IReadOnlyList<string>? NewTaskIds = null, IReadOnlyList<string>? RemovedTaskIds = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.GraphReplanned;
}
