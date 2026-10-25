namespace Agentivity.Observability.Contracts.Events.Llm;

public sealed record LlmCallStartedEvent(string Provider, string Model, string OperationKind, DateTime StartedAtUtc, string? ToolChoiceMode = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.LlmCallStarted;
}
