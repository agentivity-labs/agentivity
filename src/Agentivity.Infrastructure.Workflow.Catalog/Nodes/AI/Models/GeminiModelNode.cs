using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Recovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;

/// <summary>
/// LLM model node for Google Gemini (AI Studio).
/// Credential template: <c>gemini</c>.
/// </summary>
public sealed class GeminiModelNode : BaseLlmModelNode
{
    public GeminiModelNode(ILlmClientFactory llmClientFactory, ILogger<GeminiModelNode> logger, IOptions<LlmRecoveryOptions>? recoveryOptions = null)
        : base("ai.llm.model.gemini", "gemini", LlmApiFlavor.Gemini, llmClientFactory, logger, recoveryOptions)
    {
        DefineMetadata("Google Gemini Model", "Google Gemini models via AI Studio (gemini-2.0-flash, gemini-2.5-pro, etc.).", "ai.models", whenToUse: "To give an AI Agent, an LLM Prompt or another AI step a Google Gemini model, with an API key from Google AI Studio.");
        DefineEffects(NodeEffects.Costs);
    }

    protected override string DefaultBaseUrl => "https://generativelanguage.googleapis.com/v1beta";
}
