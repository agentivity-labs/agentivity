namespace Agentivity.Observability.Contracts.Events.Llm;

public sealed record LlmCallCompletedEvent(
    string Provider,
    string Model,
    string OperationKind,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    long DurationMs,
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int TotalTokens = 0,
    decimal EstimatedCost = 0,
    string? FinishReason = null,
    bool Succeeded = true,
    string? ErrorMessage = null
) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.LlmCallCompleted;
}
