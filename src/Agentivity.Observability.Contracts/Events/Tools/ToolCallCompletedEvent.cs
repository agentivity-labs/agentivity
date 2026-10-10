namespace Agentivity.Observability.Contracts.Events.Tools;

public sealed record ToolCallCompletedEvent(string ToolName, DateTime StartedAtUtc, DateTime CompletedAtUtc, long DurationMs, bool Succeeded, bool ReusedFromState = false, string? ToolProvider = null, string? ArgumentsHash = null, string? ResultHash = null, string? ErrorMessage = null)
    : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.ToolCallCompleted;
}
