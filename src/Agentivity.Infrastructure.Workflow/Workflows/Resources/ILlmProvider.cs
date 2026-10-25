using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Interface for LLM model resource nodes.
/// The node is the client — consumers resolve the node and call it directly.
/// No credential resolution needed on the consumer side.
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// Sends a completion request to the LLM using this node's credentials and configuration.
    /// </summary>
    /// <param name="binding">Resource binding containing this node's configuration.</param>
    /// <param name="prompt">The user prompt.</param>
    /// <param name="options">Optional overrides for temperature, max tokens, system prompt, etc.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The LLM completion result.</returns>
    ValueTask<OperationResult<LlmResponse>> CompleteAsync(ResourceBinding binding, string prompt, LlmRequestOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a chat-conversation request to the LLM using this node's credentials and configuration.
    /// Supports tool calling for agentic runtimes.
    /// </summary>
    ValueTask<OperationResult<LlmConversationResponse>> CompleteConversationAsync(ResourceBinding binding, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools = null, LlmConversationOptions? options = null, CancellationToken cancellationToken = default);
}
