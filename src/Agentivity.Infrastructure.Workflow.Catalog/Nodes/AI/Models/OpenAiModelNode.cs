using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Recovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;

/// <summary>
/// LLM model node for OpenAI (and OpenAI-compatible APIs: Azure OpenAI, LM Studio, Ollama, Groq…).
/// Credential template: <c>openai</c>.
/// </summary>
public sealed class OpenAiModelNode : BaseLlmModelNode
{
    public OpenAiModelNode(ILlmClientFactory llmClientFactory, ILogger<OpenAiModelNode> logger, IOptions<LlmRecoveryOptions>? recoveryOptions = null)
        : base("ai.llm.model.openai", "openai", LlmApiFlavor.OpenAI, llmClientFactory, logger, recoveryOptions)
    {
        DefineMetadata("OpenAI Model", "OpenAI GPT models (and compatible: LM Studio, Ollama, Groq, Azure OpenAI…).", "ai.models", whenToUse: "To give an AI Agent, an LLM Prompt or another AI step a GPT model to work with. Also for services that follow the OpenAI API, such as Azure OpenAI, Groq, or a model on your own machine with LM Studio or Ollama.");
        DefineEffects(NodeEffects.Costs);
    }
}
