using Agentivity.Domain.Workflow;
using System.Text;
using System.Text.Json;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.VectorStores;

/// <summary>
/// Pinecone Vector Store — same role as <see cref="QdrantVectorStoreNode"/>, in a Pinecone index (a namespace of it is the
/// collection). The index is created by its owner, with a fixed vector size: vectors of another size are refused before being sent.
/// Pinecone accepts any string as a vector id.
/// </summary>
public sealed class PineconeVectorStoreNode : ResourceNode, IVectorStoreProvider
{
    private const string TextField = "text";

    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PineconeVectorStoreNode> _logger;

    public PineconeVectorStoreNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<PineconeVectorStoreNode> logger)
        : base("ai.vectorstore.pinecone")
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.VectorStore);

        DefineMetadata("Pinecone Vector Store", "Texts kept with their vectors in a Pinecone index, and found again by meaning.", "ai.vectorstore", whenToUse: "To keep texts in your Pinecone index and find them again by meaning, from one run to the next. Needs your Pinecone connection and an index whose size matches the Embeddings model; for a free option without any account, use In-Memory Vector Store. For what an agent should know (documents, pages, feeds), use a Knowledge Base instead.");
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.WritesExternal | NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Vector Store", ResourcePortCategory.VectorStore, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "The model that makes the vectors (optional; the free Local Embeddings model when nothing is connected). Its size must be the index's.", isRequired: false);

        DefineCredentialsInput("pinecone_credentials", "pinecone", "Pinecone index connection", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("namespace", ArgumentType.Text, "Pinecone namespace: the collection within the index (leave empty for the default namespace).", "", ui: new ArgumentUiMetadata { Group = "Collection", Order = 20 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public async Task UpsertAsync(ResourceBinding binding, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
            return;
        var connection = await ConnectAsync(binding, cancellationToken).ConfigureAwait(false);
        var size = await SizeAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
            Check(size, record.Vector.Length);

        var vectors = records.Select(r =>
        {
            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { [TextField] = r.Text };
            foreach (var (key, value) in r.Metadata ?? new Dictionary<string, string>())
                metadata[key] = value;
            return new { id = r.Id, values = r.Vector, metadata };
        });
        var response = await connection.Client.PostAsync($"{connection.Host}/vectors/upsert", Body(new { vectors, @namespace = connection.Namespace }), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "insert vectors", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] query, int limit, IReadOnlyDictionary<string, string>? filter = null, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectAsync(binding, cancellationToken).ConfigureAwait(false);
        Check(await SizeAsync(connection, cancellationToken).ConfigureAwait(false), query.Length);

        object body = filter is { Count: > 0 }
            ? new { vector = query, topK = limit, includeMetadata = true, includeValues = false, @namespace = connection.Namespace, filter = filter.ToDictionary(f => f.Key, f => (object)new Dictionary<string, string> { ["$eq"] = f.Value }) }
            : new { vector = query, topK = limit, includeMetadata = true, includeValues = false, @namespace = connection.Namespace };
        var response = await connection.Client.PostAsync($"{connection.Host}/query", Body(body), cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await EnsureSuccessAsync(response, "search vectors", cancellationToken).ConfigureAwait(false));

        var matches = new List<VectorMatch>();
        if (!document.RootElement.TryGetProperty("matches", out var results))
            return matches;
        foreach (var item in results.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? string.Empty;
            var score = item.TryGetProperty("score", out var scoreElement) ? scoreElement.GetSingle() : 0f;
            var text = string.Empty;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in metadataElement.EnumerateObject())
                {
                    var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
                    if (property.Name == TextField)
                        text = value;
                    else
                        metadata[property.Name] = value;
                }
            }

            matches.Add(new VectorMatch(id, text, score, metadata.Count == 0 ? null : metadata));
        }

        return matches;
    }

    public async Task DeleteAsync(ResourceBinding binding, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return;
        var connection = await ConnectAsync(binding, cancellationToken).ConfigureAwait(false);
        var response = await connection.Client.PostAsync($"{connection.Host}/vectors/delete", Body(new { ids, @namespace = connection.Namespace }), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "delete vectors", cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectAsync(binding, cancellationToken).ConfigureAwait(false);
        var response = await connection.Client.PostAsync($"{connection.Host}/vectors/delete", Body(new { deleteAll = true, @namespace = connection.Namespace }), cancellationToken).ConfigureAwait(false);
        // An empty namespace is not there yet: there is nothing to remove.
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, "empty namespace", cancellationToken).ConfigureAwait(false);
    }

    // ── Connection ───────────────────────────────────────────────────────

    private sealed record Connection(HttpClient Client, string Host, string? Namespace);

    private async Task<Connection> ConnectAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        var credentialsId = binding.RequireString("pinecone_credentials");
        var credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");
        var host = credentials.GetField("host") ?? throw new InvalidOperationException("Pinecone credentials missing 'host' field.");
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            host = $"https://{host}";
        var apiKey = credentials.GetField("api_key") ?? throw new InvalidOperationException("Pinecone credentials missing 'api_key' field.");
        var ns = binding.GetString("namespace");

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Remove("Api-Key");
        client.DefaultRequestHeaders.Add("Api-Key", apiKey);
        return new Connection(client, host.TrimEnd('/'), string.IsNullOrWhiteSpace(ns) ? null : ns.Trim());
    }

    /// <summary>The vector size of the index (Pinecone fixes it when the index is created), or null when it does not say.</summary>
    private async Task<int?> SizeAsync(Connection connection, CancellationToken cancellationToken)
    {
        var response = await connection.Client.PostAsync($"{connection.Host}/describe_index_stats", Body(new { }), cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await EnsureSuccessAsync(response, "read index", cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("dimension", out var dimension) && dimension.TryGetInt32(out var size) ? size : null;
    }

    private static void Check(int? holds, int given)
    {
        if (holds is { } size && size != given)
            throw new VectorSizeMismatchException("The Pinecone index", size, given);
    }

    private static StringContent Body(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task<string> EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Pinecone {Operation} failed. Status: {StatusCode}, Response: {Response}", operation, (int)response.StatusCode, body);
            throw new InvalidOperationException($"Pinecone {operation} failed with {response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }

        return body;
    }
}
