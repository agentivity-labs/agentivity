namespace Agentivity.Observability.Contracts.Events.Agent;

public sealed record AgentCompletedEvent(string AgentNodeId, string AgentMode, DateTime StartedAtUtc, DateTime CompletedAtUtc, int IterationCount, long TotalTokens = 0, decimal EstimatedCost = 0) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.AgentCompleted;
}
