using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Recovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;

/// <summary>
/// LLM model node for Anthropic Claude models.
/// Credential template: <c>anthropic</c>.
/// </summary>
public sealed class AnthropicModelNode : BaseLlmModelNode
{
    public AnthropicModelNode(ILlmClientFactory llmClientFactory, ILogger<AnthropicModelNode> logger, IOptions<LlmRecoveryOptions>? recoveryOptions = null)
        : base("ai.llm.model.anthropic", "anthropic", LlmApiFlavor.Anthropic, llmClientFactory, logger, recoveryOptions)
    {
        DefineMetadata("Anthropic Model", "Anthropic Claude models (claude-opus-4, claude-sonnet-4, etc.).", "ai.models", whenToUse: "To give an AI Agent, an LLM Prompt or another AI step a Claude model, with a key from your Anthropic account. If your company reaches Claude through AWS, use AWS Bedrock Model instead.");
        DefineEffects(NodeEffects.Costs);
    }
}
