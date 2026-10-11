using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Recovery;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;

/// <summary>
/// Shared base for all provider-specific LLM model nodes.
/// Each concrete node supplies its credential template ID and wire-format flavor.
/// </summary>
public abstract class BaseLlmModelNode : ResourceNode, ILlmProvider
{
    private readonly ILlmClientFactory _llmClientFactory;
    private readonly ILogger _logger;
    private readonly LlmApiFlavor _flavor;
    private readonly LlmResponseRecoveryPipeline _recoveryPipeline;

    protected BaseLlmModelNode(string nodeId, string credentialsTemplateId, LlmApiFlavor flavor, ILlmClientFactory llmClientFactory, ILogger logger, IOptions<LlmRecoveryOptions>? recoveryOptions = null)
        : base(nodeId)
    {
        _llmClientFactory = llmClientFactory ?? throw new ArgumentNullException(nameof(llmClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _flavor = flavor;
        _recoveryPipeline = new LlmResponseRecoveryPipeline(DefaultLlmResponseValidator.Instance, [ReasoningContentRecoveryHandler.Instance], _logger, recoveryOptions?.Value.MaxRetries ?? 2);

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.LlmModel);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "LLM Model", ResourcePortCategory.LlmModel, maxConnections: -1);

        DefineCredentialsInput("credentials", credentialsTemplateId, "Provider credentials", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("model", ArgumentType.Text, "Model override (leave empty to use credential default)", "", ui: new ArgumentUiMetadata { Group = "Defaults", Order = 20 }, required: false);

        DefineInput("max_tokens", ArgumentType.Number, "Maximum tokens in response (leave empty to let the model decide)", ui: new ArgumentUiMetadata { Group = "Defaults", Order = 30 }, required: false);
        DefineInput("top_p", ArgumentType.Number, "Nucleus sampling 0.0–1.0 (default: 1.0)", 1.0, ui: new ArgumentUiMetadata { Group = "Sampling", Order = 50 }, required: false);
        DefineInput("frequency_penalty", ArgumentType.Number, "Reduce repetition –2.0 to 2.0 (default: 0.0)", 0.0, ui: new ArgumentUiMetadata { Group = "Advanced", Order = 60 }, required: false);
        DefineInput("presence_penalty", ArgumentType.Number, "Encourage new topics –2.0 to 2.0 (default: 0.0)", 0.0, ui: new ArgumentUiMetadata { Group = "Advanced", Order = 70 }, required: false);
        DefineInput("context_length", ArgumentType.Number, "Context window override in tokens (LM Studio / Ollama only)", ui: new ArgumentUiMetadata { Group = "Advanced", Order = 80 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimodel"));
    }

    // Fallback used only when nothing upstream (e.g. AiNode's own "temperature" input) supplied
    // a value at all — temperature is a generic sampling knob, not a model-specific constraint,
    // so it deliberately has no per-model override here (unlike max_tokens, whose real ceiling
    // does vary by model).
    private const double DefaultTemperature = 0.7;

    public async ValueTask<OperationResult<LlmResponse>> CompleteAsync(ResourceBinding binding, string prompt, LlmRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ILlmClient client = await CreateClientAsync(binding, cancellationToken).ConfigureAwait(false);

        int? defaultMaxTokens = binding.GetInt("max_tokens");
        int? defaultContextLength = binding.GetInt("context_length");

        LlmRequestOptions opts = options ?? new LlmRequestOptions();

        return await client.CompleteAsync(prompt, opts.SystemPrompt, opts.Temperature ?? DefaultTemperature, opts.MaxTokens ?? defaultMaxTokens, opts.ResponseSchema, null, opts.ContextLength ?? defaultContextLength, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<OperationResult<LlmConversationResponse>> CompleteConversationAsync(ResourceBinding binding, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools = null, LlmConversationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ILlmClient client = await CreateClientAsync(binding, cancellationToken).ConfigureAwait(false);

        int? defaultMaxTokens = binding.GetInt("max_tokens");
        int? defaultContextLength = binding.GetInt("context_length");

        LlmConversationOptions opts = options ?? new LlmConversationOptions();
        opts = opts with { Temperature = opts.Temperature ?? DefaultTemperature, MaxTokens = opts.MaxTokens ?? defaultMaxTokens, ContextLength = opts.ContextLength ?? defaultContextLength };

        return await _recoveryPipeline.ExecuteWithRecoveryAsync((msgs, tls, eopts, ct) => client.CompleteConversationAsync(msgs, tls, eopts, null, ct), messages, tools, opts, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ILlmClient> CreateClientAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        string credentialsId = binding.RequireString("credentials");
        string? modelOverride = binding.GetString("model");
        return _llmClientFactory.CreateAsync(credentialsId, modelOverride, _flavor, DefaultBaseUrl, cancellationToken);
    }

    /// <summary>Fallback base URL when the credential does not supply one.</summary>
    protected abstract string DefaultBaseUrl { get; }
}
