namespace Agentivity.Infrastructure.Core.Llm.Recovery;

/// <summary>
/// Default response validator: a response is invalid when it has no textual content and no tool calls.
/// </summary>
public sealed class DefaultLlmResponseValidator : ILlmResponseValidator
{
    public static DefaultLlmResponseValidator Instance { get; } = new();

    public bool IsValid(LlmConversationResponse response) => !string.IsNullOrWhiteSpace(response.Content) || response.ToolCalls.Count > 0;
}
