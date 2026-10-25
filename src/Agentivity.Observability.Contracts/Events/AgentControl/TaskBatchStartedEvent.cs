namespace Agentivity.Observability.Contracts.Events.AgentControl;

/// <summary>
/// Emitted when the P&amp;E executor dispatches a batch of ready tasks for parallel execution.
/// </summary>
public sealed record TaskBatchStartedEvent(int BatchIndex, int TaskCount, DateTime OccurredAtUtc, IReadOnlyList<string>? TaskIds = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.TaskBatchStarted;
}
