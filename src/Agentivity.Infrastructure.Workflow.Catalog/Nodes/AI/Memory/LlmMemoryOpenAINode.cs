using Agentivity.Domain.Credentials;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models.Capabilities;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Memory;

/// <summary>
/// OpenAI Memory Node — the node IS the embedding provider by default (dedicated OpenAI
/// credentials), but falls back to the free shared local embedding when no credentials are set,
/// and accepts an optional "embeddings" resource port to override either with any other provider.
/// </summary>
public sealed class LlmMemoryOpenAINode : ResourceNode, IEmbeddingProvider
{
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmMemoryOpenAINode> _logger;
    private readonly LocalEmbeddingService _local;

    public LlmMemoryOpenAINode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, LocalEmbeddingService local, ILogger<LlmMemoryOpenAINode> logger)
        : base("ai.memory.openai")
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.LlmMemory);

        DefineMetadata("OpenAI Memory", "RAG with dedicated OpenAI credentials for embeddings and vector storage.", "ai.memory");
        DefineEffects(NodeEffects.WritesInternal | NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "LLM Memory", ResourcePortCategory.LlmMemory, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "Optional — overrides the credentials below with a connected Embeddings node.", isRequired: false);

        DefineCredentialsInput("credentials", "openai", "OpenAI credentials for embeddings — leave unset to fall back to the free local embedding", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);

        DefineChoice(
            "embedding_model",
            [("text-embedding-3-small", "text-embedding-3-small (Fast, Cheap)"), ("text-embedding-3-large", "text-embedding-3-large (Best Quality)"), ("text-embedding-ada-002", "text-embedding-ada-002 (Legacy)")],
            "Embedding model",
            "text-embedding-3-small",
            ui: new ArgumentUiMetadata { Group = "Settings", Order = 20 },
            required: false
        );

        DefineChoice("vector_store", [("local", "Local (K-means clustering)"), ("pinecone", "Pinecone (Cloud Vector DB)"), ("weaviate", "Weaviate (Self-hosted/Cloud)")], "Vector store backend", "local", ui: new ArgumentUiMetadata { Group = "Settings", Order = 30 }, required: false);

        DefineInput("namespace", ArgumentType.Text, "Namespace for vector storage", "default", ui: new ArgumentUiMetadata { Group = "Settings", Order = 40 }, required: false);
        DefineInput("max_clusters", ArgumentType.Number, "Max clusters for local mode", 30, ui: new ArgumentUiMetadata { Group = "Advanced", Order = 50 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public Task<float[]> CreateEmbeddingAsync(ResourceBinding binding, string text, CancellationToken cancellationToken = default)
    {
        (IEmbeddingProvider provider, ResourceBinding providerBinding) = Resolve(binding);
        return provider.CreateEmbeddingAsync(providerBinding, text, cancellationToken);
    }

    public Task<List<float[]>> CreateBatchEmbeddingsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        (IEmbeddingProvider provider, ResourceBinding providerBinding) = Resolve(binding);
        return provider.CreateBatchEmbeddingsAsync(providerBinding, texts, cancellationToken);
    }

    public int GetEmbeddingDimensions(ResourceBinding binding)
    {
        (IEmbeddingProvider provider, ResourceBinding providerBinding) = Resolve(binding);
        return provider.GetEmbeddingDimensions(providerBinding);
    }

    public string GetModelName(ResourceBinding binding)
    {
        (IEmbeddingProvider provider, ResourceBinding providerBinding) = Resolve(binding);
        return provider.GetModelName(providerBinding);
    }

    private (IEmbeddingProvider Provider, ResourceBinding Binding) Resolve(ResourceBinding binding)
    {
        IEmbeddingProvider? bundled = !string.IsNullOrWhiteSpace(binding.GetString("credentials"))
            ? new DelegatingEmbeddingProvider(CreateEmbeddingViaOwnCredentialsAsync, CreateBatchEmbeddingsViaOwnCredentialsAsync, GetOwnEmbeddingDimensions, GetOwnModelName)
            : null;
        return EmbeddingProviderResolver.Resolve(binding, "embeddings", bundled, _local);
    }

    // The following "OwnCredentials"/"Own..." methods are THIS node's original embedding logic —
    // called only once Resolve has decided this node's own bundled credentials should be used.

    private async Task<float[]> CreateEmbeddingViaOwnCredentialsAsync(ResourceBinding binding, string text, CancellationToken cancellationToken)
    {
        OpenAIEmbeddingCapability capability = await CreateCapabilityAsync(binding, cancellationToken).ConfigureAwait(false);
        return await capability.CreateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<float[]>> CreateBatchEmbeddingsViaOwnCredentialsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken)
    {
        OpenAIEmbeddingCapability capability = await CreateCapabilityAsync(binding, cancellationToken).ConfigureAwait(false);
        return await capability.CreateBatchEmbeddingsAsync(texts, cancellationToken).ConfigureAwait(false);
    }

    private static int GetOwnEmbeddingDimensions(ResourceBinding binding) =>
        binding.GetString("embedding_model") switch
        {
            "text-embedding-3-large" => 3072,
            _ => 1536,
        };

    private static string GetOwnModelName(ResourceBinding binding) => binding.GetString("embedding_model") ?? "text-embedding-3-small";

    private async ValueTask<OpenAIEmbeddingCapability> CreateCapabilityAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        string credentialsId = binding.RequireString("credentials");
        string model = binding.GetString("embedding_model") ?? "text-embedding-3-small";

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Credentials missing 'api_key' field.");

        string baseUrl = credentials.GetField("base_url", "https://api.openai.com/v1");

        return new OpenAIEmbeddingCapability(_httpClientFactory, apiKey, baseUrl, model, _logger);
    }
}
