using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Core.Llm.Recovery;

/// <summary>
/// Validates whether an LLM conversation response is usable by the caller.
/// </summary>
public interface ILlmResponseValidator
{
    bool IsValid(LlmConversationResponse response);
}

/// <summary>
/// A pluggable handler that attempts to recover from an invalid LLM response.
/// Handlers are tried in registration order by <see cref="LlmResponseRecoveryPipeline"/>.
/// </summary>
public interface ILlmResponseRecoveryHandler
{
    string Name { get; }

    ValueTask<LlmResponseRecoveryDecision> TryRecoverAsync(LlmResponseRecoveryContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Full context provided to a recovery handler so it can decide how to recover.
/// </summary>
public sealed record LlmResponseRecoveryContext(IReadOnlyList<LlmChatMessage> OriginalMessages, IReadOnlyList<JsonObject>? Tools, LlmConversationOptions Options, LlmConversationResponse Response);

/// <summary>
/// Discriminated-union result returned by a recovery handler.
/// </summary>
public abstract record LlmResponseRecoveryDecision
{
    private LlmResponseRecoveryDecision() { }

    /// <summary>The handler produced a repaired response directly — use it.</summary>
    public sealed record Handled(LlmConversationResponse RepairedResponse) : LlmResponseRecoveryDecision;

    /// <summary>The handler wants the LLM called again with modified parameters.</summary>
    public sealed record Retry(IReadOnlyList<LlmChatMessage>? ModifiedMessages = null, LlmConversationOptions? ModifiedOptions = null) : LlmResponseRecoveryDecision;

    /// <summary>The handler does not apply to this situation.</summary>
    public sealed record Skip(string Reason) : LlmResponseRecoveryDecision;
}
