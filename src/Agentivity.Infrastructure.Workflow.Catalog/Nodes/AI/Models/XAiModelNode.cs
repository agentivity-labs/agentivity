using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Recovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;

/// <summary>
/// LLM model node for xAI Grok models (OpenAI-compatible API).
/// Credential template: <c>xai</c>.
/// </summary>
public sealed class XAiModelNode : BaseLlmModelNode
{
    public XAiModelNode(ILlmClientFactory llmClientFactory, ILogger<XAiModelNode> logger, IOptions<LlmRecoveryOptions>? recoveryOptions = null)
        : base("ai.llm.model.xai", "xai", LlmApiFlavor.OpenAI, llmClientFactory, logger, recoveryOptions)
    {
        DefineMetadata("xAI / Grok Model", "xAI Grok models (grok-3, grok-3-mini, etc.).", "ai.models");
        DefineEffects(NodeEffects.Costs);
    }

    protected override string DefaultBaseUrl => "https://api.x.ai/v1";
}
