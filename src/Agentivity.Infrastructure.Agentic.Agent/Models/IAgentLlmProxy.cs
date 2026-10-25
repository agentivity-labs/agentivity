using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

public interface IAgentLlmProxy
{
    ValueTask<LlmConversationResponse> InvokeLlmConversationAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions? options, CancellationToken cancellationToken);

    ValueTask<AgentToolExecution> ExecuteToolCallAsync(SessionContext sessionContext, IReadOnlySet<string> reusableToolNames, LlmToolCall toolCall, CancellationToken cancellationToken);
}
