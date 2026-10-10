using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models;

/// <summary>
/// AWS Bedrock LLM model node (Anthropic Claude models). Deliberately does NOT extend
/// <see cref="BaseLlmModelNode"/> — that base class's whole credential/client-creation path
/// assumes "API key + base URL" (<c>ILlmClientFactory.CreateAsync(credentialId, ...)</c>), which
/// doesn't fit AWS SigV4 request signing (access key + secret + region, no bearer token) at all.
/// See <see cref="BedrockLlmClient"/> for the actual request/response handling.
/// <para>
/// A <see cref="BedrockLlmClient"/> owns a real <c>AmazonBedrockRuntimeClient</c> (disposable,
/// its own connection pool) — cached per credentials id rather than rebuilt on every call, same
/// reason <c>HttpClient</c> instances are normally reused. The cache is the shared
/// <see cref="BedrockClientCache"/> service, not a field of this node, so it outlives any one node
/// instance; the container disposes the clients at shutdown.
/// </para>
/// </summary>
public sealed class BedrockModelNode : ResourceNode, ILlmProvider
{
    private const string DefaultModelId = "anthropic.claude-3-5-sonnet-20241022-v2:0";
    private const double DefaultTemperature = 0.7;

    private readonly CredentialsService _credentialsService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BedrockClientCache _clients;

    public BedrockModelNode(CredentialsService credentialsService, ILoggerFactory loggerFactory, BedrockClientCache clients)
        : base("ai.llm.model.bedrock")
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.LlmModel);

        DefineMetadata("AWS Bedrock Model", "Anthropic Claude models via AWS Bedrock — SigV4-signed requests through the official AWS SDK, no bearer token.", "ai.models");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "LLM Model", ResourcePortCategory.LlmModel, maxConnections: -1);

        DefineCredentialsInput("credentials", "aws_bedrock", "AWS credentials used to sign Bedrock requests", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineInput("model", ArgumentType.Text, "Bedrock model id", DefaultModelId, ui: new ArgumentUiMetadata { Group = "Defaults", Order = 20 });
        DefineInput("max_tokens", ArgumentType.Number, "Maximum tokens in response (leave empty to let the model decide)", ui: new ArgumentUiMetadata { Group = "Defaults", Order = 30 });
        DefineInput("top_p", ArgumentType.Number, "Nucleus sampling 0.0–1.0 (default: 1.0)", 1.0, ui: new ArgumentUiMetadata { Group = "Sampling", Order = 50 });

        DefineIcon(NodeIcon.Embedded("aimodel"));
    }

    public async ValueTask<OperationResult<LlmResponse>> CompleteAsync(ResourceBinding binding, string prompt, LlmRequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        BedrockLlmClient client = await ResolveClientAsync(binding, cancellationToken).ConfigureAwait(false);
        string? model = binding.GetString("model");
        int? defaultMaxTokens = binding.GetInt("max_tokens");

        LlmRequestOptions opts = options ?? new LlmRequestOptions();
        return await client.CompleteAsync(prompt, opts.SystemPrompt, opts.Temperature ?? DefaultTemperature, opts.MaxTokens ?? defaultMaxTokens, opts.ResponseSchema, model, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<OperationResult<LlmConversationResponse>> CompleteConversationAsync(ResourceBinding binding, IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools = null, LlmConversationOptions? options = null, CancellationToken cancellationToken = default)
    {
        BedrockLlmClient client = await ResolveClientAsync(binding, cancellationToken).ConfigureAwait(false);
        string? model = binding.GetString("model");
        int? defaultMaxTokens = binding.GetInt("max_tokens");

        LlmConversationOptions opts = options ?? new LlmConversationOptions();
        opts = opts with { Temperature = opts.Temperature ?? DefaultTemperature, MaxTokens = opts.MaxTokens ?? defaultMaxTokens };

        return await client.CompleteConversationAsync(messages, tools, opts, model, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BedrockLlmClient> ResolveClientAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        string credentialsId = binding.RequireString("credentials");
        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string accessKeyId = credentials.GetField("access_key_id") ?? throw new InvalidOperationException("AWS Bedrock credentials missing 'access_key_id' field.");
        string secretAccessKey = credentials.GetField("secret_access_key") ?? throw new InvalidOperationException("AWS Bedrock credentials missing 'secret_access_key' field.");
        string region = credentials.GetField("region") ?? throw new InvalidOperationException("AWS Bedrock credentials missing 'region' field.");

        // The credentials are read on every call so that editing them takes effect at once: the cached client is reused only
        // while its fingerprint still matches what the credentials say now.
        string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{accessKeyId}\n{secretAccessKey}\n{region}")));
        return _clients.GetOrCreate(credentialsId, fingerprint, () => new BedrockLlmClient(accessKeyId, secretAccessKey, region, DefaultModelId, _loggerFactory.CreateLogger<BedrockLlmClient>()));
    }
}
