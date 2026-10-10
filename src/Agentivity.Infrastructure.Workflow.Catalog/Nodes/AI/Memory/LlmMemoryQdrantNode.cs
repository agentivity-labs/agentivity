using System.Text;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models.Capabilities;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Memory;

/// <summary>
/// Qdrant Memory Node — a real, persistent vector store (unlike ai.memory.simple/ai.memory.openai,
/// which re-embed and rank documents locally on every run). Falls back to the free shared local
/// embedding when no embedding credentials are set, same 3-tier resolution as every other memory
/// node (see <see cref="EmbeddingProviderResolver"/>) — an explicit "embeddings" port, its own
/// bundled OpenAI credentials, or the local model, in that order.
/// <para>
/// Exposes ResourcePortCategory.LlmMemory, same as every other memory node, so it plugs into
/// existing "llm_memory" consumer ports unchanged. Consumers that only know IEmbeddingProvider
/// keep working (re-embed + rank locally); WorkflowAgentMemoryProxy/AgentMemoryResolver also check
/// for IVectorStoreProvider and, when present, persist once and query Qdrant's real ANN index
/// instead.
/// </para>
/// </summary>
public sealed class LlmMemoryQdrantNode : ResourceNode, IEmbeddingProvider, IVectorStoreProvider
{
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmMemoryQdrantNode> _logger;
    private readonly LocalEmbeddingService _local;

    public LlmMemoryQdrantNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, LocalEmbeddingService local, ILogger<LlmMemoryQdrantNode> logger)
        : base("ai.memory.qdrant")
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.LlmMemory);

        DefineMetadata("Qdrant Memory", "RAG backed by a real Qdrant vector database — persists embeddings once and queries a real ANN index instead of re-ranking locally on every run.", "ai.memory");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "LLM Memory", ResourcePortCategory.LlmMemory, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "Optional — overrides the credentials below with a connected Embeddings node.", isRequired: false);

        DefineCredentialsInput("embedding_credentials", "openai", "OpenAI credentials used to embed text — leave unset to fall back to the free local embedding", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineCredentialsInput("qdrant_credentials", "qdrant", "Qdrant instance connection", ui: new ArgumentUiMetadata { Group = "Connection", Order = 20 });

        DefineChoice(
            "embedding_model",
            [("text-embedding-3-small", "text-embedding-3-small (Fast, Cheap)"), ("text-embedding-3-large", "text-embedding-3-large (Best Quality)"), ("text-embedding-ada-002", "text-embedding-ada-002 (Legacy)")],
            "Embedding model",
            "text-embedding-3-small",
            ui: new ArgumentUiMetadata { Group = "Settings", Order = 30 }
        );
        DefineInput("collection_name", ArgumentType.Text, "Qdrant collection name (created automatically on first use)", "agentivity_knowledge", ui: new ArgumentUiMetadata { Group = "Settings", Order = 40 });

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    // ── IEmbeddingProvider — external port → own bundled OpenAI credentials → local fallback ──

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
        IEmbeddingProvider? bundled = !string.IsNullOrWhiteSpace(binding.GetString("embedding_credentials"))
            ? new DelegatingEmbeddingProvider(CreateEmbeddingViaOwnCredentialsAsync, CreateBatchEmbeddingsViaOwnCredentialsAsync, GetOwnEmbeddingDimensions, GetOwnModelName)
            : null;
        return EmbeddingProviderResolver.Resolve(binding, "embeddings", bundled, _local);
    }

    // The following "Own..." methods are THIS node's original bundled-OpenAI-credentials
    // embedding logic — called only once Resolve has decided to use it.

    private async Task<float[]> CreateEmbeddingViaOwnCredentialsAsync(ResourceBinding binding, string text, CancellationToken cancellationToken)
    {
        OpenAIEmbeddingCapability capability = await CreateEmbeddingCapabilityAsync(binding, cancellationToken).ConfigureAwait(false);
        return await capability.CreateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<float[]>> CreateBatchEmbeddingsViaOwnCredentialsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken)
    {
        OpenAIEmbeddingCapability capability = await CreateEmbeddingCapabilityAsync(binding, cancellationToken).ConfigureAwait(false);
        return await capability.CreateBatchEmbeddingsAsync(texts, cancellationToken).ConfigureAwait(false);
    }

    private static int GetOwnEmbeddingDimensions(ResourceBinding binding) =>
        binding.GetString("embedding_model") switch
        {
            "text-embedding-3-large" => 3072,
            _ => 1536,
        };

    private static string GetOwnModelName(ResourceBinding binding) => binding.GetString("embedding_model") ?? "text-embedding-3-small";

    // ── IVectorStoreProvider — real Qdrant persistence + ANN search ─────────────────────────

    public async Task UpsertAsync(ResourceBinding binding, string id, float[] embedding, string text, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken = default)
    {
        (HttpClient client, string baseUrl, string collection) = await PrepareCollectionAsync(binding, embedding.Length, cancellationToken).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = text };
        if (metadata is not null)
        {
            foreach (KeyValuePair<string, string> entry in metadata)
            {
                payload[entry.Key] = entry.Value;
            }
        }

        var body = new { points = new[] { new { id = ToQdrantPointId(id), vector = embedding, payload } } };

        HttpResponseMessage response = await client.PutAsync($"{baseUrl}/collections/{collection}/points?wait=true", JsonContent(body), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "upsert point", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        (HttpClient client, string baseUrl, string collection) = await PrepareCollectionAsync(binding, queryEmbedding.Length, cancellationToken).ConfigureAwait(false);

        var body = new
        {
            vector = queryEmbedding,
            limit = topK,
            with_payload = true,
        };

        HttpResponseMessage response = await client.PostAsync($"{baseUrl}/collections/{collection}/points/search", JsonContent(body), cancellationToken).ConfigureAwait(false);
        string responseBody = await EnsureSuccessAsync(response, "query points", cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(responseBody);
        var matches = new List<VectorMatch>();

        if (document.RootElement.TryGetProperty("result", out JsonElement results))
        {
            foreach (JsonElement item in results.EnumerateArray())
            {
                string pointId = item.GetProperty("id").ToString();
                float score = item.TryGetProperty("score", out JsonElement scoreElement) ? scoreElement.GetSingle() : 0f;

                string text = string.Empty;
                Dictionary<string, string>? metadata = null;
                if (item.TryGetProperty("payload", out JsonElement payload) && payload.ValueKind == JsonValueKind.Object)
                {
                    metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonProperty property in payload.EnumerateObject())
                    {
                        if (string.Equals(property.Name, "text", StringComparison.Ordinal))
                        {
                            text = property.Value.GetString() ?? string.Empty;
                        }
                        else
                        {
                            metadata[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
                        }
                    }
                }

                matches.Add(new VectorMatch(pointId, text, score, metadata));
            }
        }

        return matches;
    }

    public async Task DeleteAsync(ResourceBinding binding, string id, CancellationToken cancellationToken = default)
    {
        (HttpClient client, string baseUrl, string collection) = await ResolveConnectionAsync(binding, cancellationToken).ConfigureAwait(false);

        var body = new { points = new[] { ToQdrantPointId(id) } };
        HttpResponseMessage response = await client.PostAsync($"{baseUrl}/collections/{collection}/points/delete?wait=true", JsonContent(body), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "delete point", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetExistingIdsAsync(ResourceBinding binding, IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new HashSet<string>();
        }

        (HttpClient client, string baseUrl, string collection) = await ResolveConnectionAsync(binding, cancellationToken).ConfigureAwait(false);

        // Ids are one-way hashed into Qdrant point ids (see ToQdrantPointId) — keep a reverse map
        // so the caller's original ids can be reported back, not our internal point ids.
        var pointIdToOriginal = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            pointIdToOriginal[ToQdrantPointId(id)] = id;
        }

        var body = new { ids = pointIdToOriginal.Keys.ToArray(), with_payload = false, with_vector = false };
        HttpResponseMessage response = await client.PostAsync($"{baseUrl}/collections/{collection}/points", JsonContent(body), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Most likely the collection doesn't exist yet (first run for this memory node) —
            // that means nothing is indexed yet, not a failure worth surfacing to the caller.
            return new HashSet<string>();
        }

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(responseBody);

        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("result", out JsonElement results))
        {
            foreach (JsonElement item in results.EnumerateArray())
            {
                string pointId = item.GetProperty("id").ToString();
                if (pointIdToOriginal.TryGetValue(pointId, out string? originalId))
                {
                    existing.Add(originalId);
                }
            }
        }

        return existing;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────────────────

    private async ValueTask<OpenAIEmbeddingCapability> CreateEmbeddingCapabilityAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        string credentialsId = binding.RequireString("embedding_credentials");
        string model = binding.GetString("embedding_model") ?? "text-embedding-3-small";

        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Embedding credentials missing 'api_key' field.");
        string baseUrl = credentials.GetField("base_url", "https://api.openai.com/v1");

        return new OpenAIEmbeddingCapability(_httpClientFactory, apiKey, baseUrl, model, _logger);
    }

    private async Task<(HttpClient Client, string BaseUrl, string Collection)> ResolveConnectionAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        string credentialsId = binding.RequireString("qdrant_credentials");
        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string baseUrl = credentials.GetField("base_url")?.TrimEnd('/') ?? throw new InvalidOperationException("Qdrant credentials missing 'base_url' field.");
        string? apiKey = credentials.GetField("api_key");
        string collection = binding.GetString("collection_name") ?? "agentivity_knowledge";

        HttpClient client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Remove("api-key");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Add("api-key", apiKey);
        }

        return (client, baseUrl, collection);
    }

    private async Task<(HttpClient Client, string BaseUrl, string Collection)> PrepareCollectionAsync(ResourceBinding binding, int vectorSize, CancellationToken cancellationToken)
    {
        (HttpClient client, string baseUrl, string collection) = await ResolveConnectionAsync(binding, cancellationToken).ConfigureAwait(false);

        HttpResponseMessage existing = await client.GetAsync($"{baseUrl}/collections/{collection}", cancellationToken).ConfigureAwait(false);
        if (existing.IsSuccessStatusCode)
        {
            return (client, baseUrl, collection);
        }

        var createBody = new { vectors = new { size = vectorSize, distance = "Cosine" } };
        HttpResponseMessage created = await client.PutAsync($"{baseUrl}/collections/{collection}", JsonContent(createBody), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(created, "create collection", cancellationToken).ConfigureAwait(false);

        return (client, baseUrl, collection);
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    // Qdrant point IDs must be an unsigned integer or a UUID — an arbitrary caller-supplied id
    // (e.g. a content hash) is neither, so deterministically derive a stable UUID from it. Must
    // stay deterministic (same input -> same UUID every call) so re-upserting the same id updates
    // the existing point instead of creating a duplicate.
    private static string ToQdrantPointId(string id)
    {
        if (Guid.TryParse(id, out Guid parsed))
        {
            return parsed.ToString();
        }

        byte[] hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(id));
        return new Guid(hash).ToString();
    }

    private async Task<string> EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Qdrant {Operation} failed. Status: {StatusCode}, Response: {Response}", operation, (int)response.StatusCode, body);
            throw new InvalidOperationException($"Qdrant {operation} failed with {response.StatusCode}: {body.Substring(0, Math.Min(200, body.Length))}");
        }

        return body;
    }
}
