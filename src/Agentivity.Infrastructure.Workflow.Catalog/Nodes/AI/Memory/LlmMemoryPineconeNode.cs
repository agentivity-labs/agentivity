using Agentivity.Domain.Workflow;
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
/// Pinecone Memory Node — a real, persistent vector store, same role as
/// <see cref="LlmMemoryQdrantNode"/> for a Pinecone-hosted index instead of self-hosted Qdrant.
/// Falls back to the free shared local embedding when no embedding credentials are set, same
/// 3-tier resolution as every other memory node (see <see cref="EmbeddingProviderResolver"/>).
/// <para>
/// Unlike Qdrant, Pinecone accepts arbitrary string vector IDs directly — no UUID coercion needed.
/// </para>
/// </summary>
public sealed class LlmMemoryPineconeNode : ResourceNode, IEmbeddingProvider, IVectorStoreProvider
{
    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmMemoryPineconeNode> _logger;
    private readonly LocalEmbeddingService _local;

    public LlmMemoryPineconeNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, LocalEmbeddingService local, ILogger<LlmMemoryPineconeNode> logger)
        : base("ai.memory.pinecone")
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.LlmMemory);

        DefineMetadata("Pinecone Memory", "RAG backed by a real Pinecone index — persists embeddings once and queries a real ANN index instead of re-ranking locally on every run.", "ai.memory", whenToUse: "To let an AI Agent or LLM Prompt search large knowledge sources stored once in a Pinecone index and reused across runs. Needs your Pinecone connection; for a free option without any account, use Simple LLM Memory.");
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.WritesExternal | NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "LLM Memory", ResourcePortCategory.LlmMemory, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "Optional — overrides the credentials below with a connected Embeddings node.", isRequired: false);

        DefineCredentialsInput("embedding_credentials", "openai", "OpenAI credentials used to embed text — leave unset to fall back to the free local embedding", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineCredentialsInput("pinecone_credentials", "pinecone", "Pinecone index connection", ui: new ArgumentUiMetadata { Group = "Connection", Order = 20 }, required: true);

        DefineChoice(
            "embedding_model",
            [("text-embedding-3-small", "text-embedding-3-small (Fast, Cheap)"), ("text-embedding-3-large", "text-embedding-3-large (Best Quality)"), ("text-embedding-ada-002", "text-embedding-ada-002 (Legacy)")],
            "Embedding model",
            "text-embedding-3-small",
            ui: new ArgumentUiMetadata { Group = "Settings", Order = 30 },
            required: false
        );
        DefineInput("namespace", ArgumentType.Text, "Pinecone namespace (leave empty for the default namespace)", "", ui: new ArgumentUiMetadata { Group = "Settings", Order = 40 }, required: false);

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

    // ── IVectorStoreProvider — real Pinecone persistence + ANN search ───────────────────────

    public async Task UpsertAsync(ResourceBinding binding, string id, float[] embedding, string text, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken = default)
    {
        (HttpClient client, string host, string? ns) = await ResolveConnectionAsync(binding, cancellationToken).ConfigureAwait(false);

        var metadataPayload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = text };
        if (metadata is not null)
        {
            foreach (KeyValuePair<string, string> entry in metadata)
            {
                metadataPayload[entry.Key] = entry.Value;
            }
        }

        var body = new { vectors = new[] { new { id, values = embedding, metadata = metadataPayload } }, @namespace = ns };
        HttpResponseMessage response = await client.PostAsync($"{host}/vectors/upsert", JsonContent(body), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "upsert vector", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        (HttpClient client, string host, string? ns) = await ResolveConnectionAsync(binding, cancellationToken).ConfigureAwait(false);

        var body = new
        {
            vector = queryEmbedding,
            topK,
            includeMetadata = true,
            includeValues = false,
            @namespace = ns,
        };

        HttpResponseMessage response = await client.PostAsync($"{host}/query", JsonContent(body), cancellationToken).ConfigureAwait(false);
        string responseBody = await EnsureSuccessAsync(response, "query vectors", cancellationToken).ConfigureAwait(false);

        using JsonDocument document = JsonDocument.Parse(responseBody);
        var matches = new List<VectorMatch>();

        if (document.RootElement.TryGetProperty("matches", out JsonElement results))
        {
            foreach (JsonElement item in results.EnumerateArray())
            {
                string id = item.GetProperty("id").GetString() ?? string.Empty;
                float score = item.TryGetProperty("score", out JsonElement scoreElement) ? scoreElement.GetSingle() : 0f;

                string text = string.Empty;
                Dictionary<string, string>? metadata = null;
                if (item.TryGetProperty("metadata", out JsonElement metadataElement) && metadataElement.ValueKind == JsonValueKind.Object)
                {
                    metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (JsonProperty property in metadataElement.EnumerateObject())
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

                matches.Add(new VectorMatch(id, text, score, metadata));
            }
        }

        return matches;
    }

    public async Task DeleteAsync(ResourceBinding binding, string id, CancellationToken cancellationToken = default)
    {
        (HttpClient client, string host, string? ns) = await ResolveConnectionAsync(binding, cancellationToken).ConfigureAwait(false);

        var body = new { ids = new[] { id }, @namespace = ns };
        HttpResponseMessage response = await client.PostAsync($"{host}/vectors/delete", JsonContent(body), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "delete vector", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetExistingIdsAsync(ResourceBinding binding, IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new HashSet<string>();
        }

        (HttpClient client, string host, string? ns) = await ResolveConnectionAsync(binding, cancellationToken).ConfigureAwait(false);

        var query = string.Join('&', ids.Select(id => $"ids={Uri.EscapeDataString(id)}"));
        var nsQuery = string.IsNullOrWhiteSpace(ns) ? string.Empty : $"&namespace={Uri.EscapeDataString(ns)}";
        HttpResponseMessage response = await client.GetAsync($"{host}/vectors/fetch?{query}{nsQuery}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Most likely the index has no vectors matching yet — treat as "nothing indexed".
            return new HashSet<string>();
        }

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(responseBody);

        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("vectors", out JsonElement vectors) && vectors.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in vectors.EnumerateObject())
            {
                existing.Add(property.Name);
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

    private async Task<(HttpClient Client, string Host, string? Namespace)> ResolveConnectionAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        string credentialsId = binding.RequireString("pinecone_credentials");
        Credentials credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");

        string host = credentials.GetField("host") ?? throw new InvalidOperationException("Pinecone credentials missing 'host' field.");
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            host = $"https://{host}";
        }

        string apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Pinecone credentials missing 'api_key' field.");
        string? ns = binding.GetString("namespace");

        HttpClient client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Remove("Api-Key");
        client.DefaultRequestHeaders.Add("Api-Key", apiKey);

        return (client, host.TrimEnd('/'), string.IsNullOrWhiteSpace(ns) ? null : ns);
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<string> EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Pinecone {Operation} failed. Status: {StatusCode}, Response: {Response}", operation, (int)response.StatusCode, body);
            throw new InvalidOperationException($"Pinecone {operation} failed with {response.StatusCode}: {body.Substring(0, Math.Min(200, body.Length))}");
        }

        return body;
    }
}
