using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

public static class LlmChatMessageCloner
{
    public static LlmChatMessage CloneMessage(LlmChatMessage message) => new(message.Role, message.Content, message.ToolCallId, message.ToolCalls?.Select(toolCall => new LlmToolCall(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson)).ToList(), Images: message.Images);
}
