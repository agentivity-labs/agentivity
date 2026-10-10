namespace Agentivity.Infrastructure.Core.Llm.Memory;

/// <summary>
/// Represents a message in the LLM conversation history
/// </summary>
public sealed class LlmMessage
{
    public string Role { get; set; } = string.Empty; // "system", "user", "assistant"
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Provider for managing LLM conversation memory/context
/// </summary>
public interface ILlmMemoryProvider
{
    /// <summary>
    /// Provider name (e.g., "InMemory", "Persistent")
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Adds a message to the conversation history
    /// </summary>
    ValueTask AddMessageAsync(string sessionId, LlmMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all messages for a session
    /// </summary>
    ValueTask<IReadOnlyList<LlmMessage>> GetMessagesAsync(string sessionId, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets formatted context for LLM (concatenated messages)
    /// </summary>
    ValueTask<string> GetContextAsync(string sessionId, int? maxMessages = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears conversation history for a session
    /// </summary>
    ValueTask ClearAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the number of messages in a session
    /// </summary>
    ValueTask<int> GetMessageCountAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all session IDs
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default);
}
