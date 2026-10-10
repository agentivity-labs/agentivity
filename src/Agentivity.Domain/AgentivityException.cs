namespace Agentivity.Domain;

/// <summary>
/// Signals a deviation from what a call expected — not necessarily a bug in our own code, but an
/// abnormal outcome that must not be silently absorbed into a return value nobody checks (e.g. an
/// LLM call truncated by its own token budget). Thrown close to where the deviation is detected,
/// caught at the nearest boundary that can meaningfully react to it (typically a graph task's
/// execution boundary) and log it — never re-thrown further than necessary, never flattened to a
/// bare string before that boundary.
/// </summary>
public abstract class AgentivityException(string code, string message, bool isRetryable = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Short, stable identifier for programmatic classification (e.g. "llm_truncated").</summary>
    public string Code { get; } = code;

    public bool IsRetryable { get; } = isRetryable;
}
