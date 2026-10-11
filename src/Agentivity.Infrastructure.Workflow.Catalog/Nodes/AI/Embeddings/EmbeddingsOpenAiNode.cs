using Agentivity.Domain.Credentials;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models.Capabilities;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Embeddings;

/// <summary>
/// Standalone OpenAI Embeddings node — same API and models as the embedding step bundled directly
/// into <c>LlmMemoryOpenAINode</c>/<c>LlmMemoryQdrantNode</c>/etc., but exposed on its own so it
/// can be swapped independently of whichever vector store consumes it (or reused across several).
/// Exposes <see cref="ResourcePortCategory.Embeddings"/>.
/// </summary>
public sealed class EmbeddingsOpenAiNode : ResourceNode, IEmbeddingProvider
{
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EmbeddingsOpenAiNode> _logger;

    public EmbeddingsOpenAiNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<EmbeddingsOpenAiNode> logger)
        : base("ai.embeddings.openai")
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.Embeddings);

        DefineMetadata("OpenAI Embeddings", "Embeddings via the OpenAI API — usable standalone or plugged into any vector store's \"embeddings\" input.", "ai.embeddings");
        DefineEffects(NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Embeddings", ResourcePortCategory.Embeddings, maxConnections: -1);

        DefineCredentialsInput("credentials", "openai", "OpenAI credentials used to embed text", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineChoice(
            "embedding_model",
            [("text-embedding-3-small", "text-embedding-3-small (Fast, Cheap)"), ("text-embedding-3-large", "text-embedding-3-large (Best Quality)"), ("text-embedding-ada-002", "text-embedding-ada-002 (Legacy)")],
            "Embedding model",
            "text-embedding-3-small",
            ui: new ArgumentUiMetadata { Group = "Settings", Order = 20 },
            required: false
        );

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public async Task<float[]> CreateEmbeddingAsync(ResourceBinding binding, string text, CancellationToken cancellationToken = default)
    {
        OpenAIEmbeddingCapability capability = await CreateCapabilityAsync(binding, cancellationToken).ConfigureAwait(false);
        return await capability.CreateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<float[]>> CreateBatchEmbeddingsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        OpenAIEmbeddingCapability capability = await CreateCapabilityAsync(binding, cancellationToken).ConfigureAwait(false);
        return await capability.CreateBatchEmbeddingsAsync(texts, cancellationToken).ConfigureAwait(false);
    }

    public int GetEmbeddingDimensions(ResourceBinding binding) =>
        binding.GetString("embedding_model") switch
        {
            "text-embedding-3-large" => 3072,
            _ => 1536,
        };

    public string GetModelName(ResourceBinding binding) => binding.GetString("embedding_model") ?? "text-embedding-3-small";

    private async ValueTask<OpenAIEmbeddingCapability> CreateCapabilityAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        string credentialsId = binding.RequireString("credentials");
        string model = binding.GetString("embedding_model") ?? "text-embedding-3-small";

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Embedding credentials missing 'api_key' field.");
        string baseUrl = credentials.GetField("base_url", "https://api.openai.com/v1");

        return new OpenAIEmbeddingCapability(_httpClientFactory, apiKey, baseUrl, model, _logger);
    }
}
