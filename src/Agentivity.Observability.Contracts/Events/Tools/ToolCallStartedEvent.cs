namespace Agentivity.Observability.Contracts.Events.Tools;

public sealed record ToolCallStartedEvent(string ToolName, DateTime StartedAtUtc, string? ToolProvider = null, string? ArgumentsHash = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.ToolCallStarted;
}
