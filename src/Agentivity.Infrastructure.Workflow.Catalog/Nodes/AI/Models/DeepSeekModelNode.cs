using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Recovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;

/// <summary>
/// LLM model node for DeepSeek models (OpenAI-compatible API).
/// Credential template: <c>deepseek</c>.
/// </summary>
public sealed class DeepSeekModelNode : BaseLlmModelNode
{
    public DeepSeekModelNode(ILlmClientFactory llmClientFactory, ILogger<DeepSeekModelNode> logger, IOptions<LlmRecoveryOptions>? recoveryOptions = null)
        : base("ai.llm.model.deepseek", "deepseek", LlmApiFlavor.OpenAI, llmClientFactory, logger, recoveryOptions)
    {
        DefineMetadata("DeepSeek Model", "DeepSeek AI models (deepseek-chat, deepseek-reasoner, etc.).", "ai.models", whenToUse: "To give an AI Agent, an LLM Prompt or another AI step a DeepSeek model, with a key from your DeepSeek account.");
        DefineEffects(NodeEffects.Costs);
    }
}
