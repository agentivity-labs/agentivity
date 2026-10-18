using System.Text.Json.Nodes;
using Agentivity.Domain.Common;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Client for a single LLM completion endpoint, resolved from a credential by
/// <see cref="ILlmClientFactory"/>. <see cref="LlmClient"/> is the production
/// implementation (OpenAI/Anthropic/Gemini wire formats over HTTP); test code can provide
/// scripted implementations to exercise agent/team pipelines deterministically.
/// </summary>
public interface ILlmClient
{
    Task<OperationResult<LlmResponse>> CompleteAsync(string prompt, string? systemPrompt = null, double temperature = 0.7, int? maxTokens = null, string? responseSchema = null, string? modelOverride = null, int? contextLength = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="CompleteAsync(string,string?,double,int?,string?,string?,int?,CancellationToken)"/>
    /// but with images attached to the user turn, for a vision-capable model. Default
    /// implementation ignores the images and falls back to the text-only overload — only a client
    /// that actually knows how to serialize images for its wire format needs to override this
    /// (see <see cref="LlmClient"/>); every existing <see cref="ILlmClient"/> implementation
    /// (test stubs included) keeps compiling and behaving exactly as before without any changes.
    /// </summary>
    Task<OperationResult<LlmResponse>> CompleteAsync(string prompt, IReadOnlyList<ImageAttachment> images, string? systemPrompt = null, double temperature = 0.7, int? maxTokens = null, string? responseSchema = null, string? modelOverride = null, int? contextLength = null, CancellationToken cancellationToken = default) =>
        CompleteAsync(prompt, systemPrompt, temperature, maxTokens, responseSchema, modelOverride, contextLength, cancellationToken);

    Task<OperationResult<LlmConversationResponse>> CompleteConversationAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools = null, LlmConversationOptions? options = null, string? modelOverride = null, CancellationToken cancellationToken = default);
}
