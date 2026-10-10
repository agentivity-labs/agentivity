namespace Agentivity.Domain;

/// <summary>
/// An LLM call deviated from what the caller expected — e.g. cut off by its own max_tokens
/// budget (finish_reason "length"/"max_tokens") or blocked by a provider's content filter.
/// Carries enough provider context (<see cref="Provider"/>/<see cref="Model"/>/
/// <see cref="FinishReason"/>) for a catcher to react or log without re-parsing the raw response.
/// </summary>
public sealed class LlmCallException(string code, string message, string provider, string model, string? finishReason, bool isRetryable = false, Exception? innerException = null)
    : AgentivityException(code, message, isRetryable, innerException)
{
    public string Provider { get; } = provider;
    public string Model { get; } = model;
    public string? FinishReason { get; } = finishReason;
}
