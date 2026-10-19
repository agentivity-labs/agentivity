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
/// Qdrant Vector Store — texts and their vectors kept in a Qdrant collection, searched with its index. The collection is created
/// at the first insert, with the size of the vectors of the Embeddings connected to the store; emptying it removes it, so it can be
/// filled again with another model. Qdrant point ids are numbers or UUIDs: the caller's id is turned into a stable UUID, and kept in
/// the payload to be given back.
/// </summary>
public sealed class QdrantVectorStoreNode : ResourceNode, IVectorStoreProvider
{
    private const string TextField = "text";
    private const string IdField = "document_id";

    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QdrantVectorStoreNode> _logger;

    public QdrantVectorStoreNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, ILogger<QdrantVectorStoreNode> logger)
        : base("ai.vectorstore.qdrant")
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.VectorStore);

        DefineMetadata("Qdrant Vector Store", "Texts kept with their vectors in a Qdrant database, and found again by meaning.", "ai.vectorstore", whenToUse: "To keep texts in your Qdrant database and find them again by meaning, from one run to the next. Needs your Qdrant connection; the collection is created at the first insert. For what an agent should know (documents, pages, feeds), use a Knowledge Base instead.");
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.WritesExternal | NodeEffects.Costs);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Vector Store", ResourcePortCategory.VectorStore, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "The model that makes the vectors (optional; the free Local Embeddings model when nothing is connected). Keep the same one for a collection.", isRequired: false);

        DefineCredentialsInput("qdrant_credentials", "qdrant", "Qdrant instance connection", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 }, required: true);
        DefineInput("collection_name", ArgumentType.Text, "Qdrant collection name (created at the first insert).", "agentivity", ui: new ArgumentUiMetadata { Group = "Collection", Order = 20 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public async Task UpsertAsync(ResourceBinding binding, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
            return;
        var connection = await ConnectAsync(binding, cancellationToken).ConfigureAwait(false);
        var size = await SizeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (size is null)
        {
            var created = await connection.Client.PutAsync($"{connection.BaseUrl}/collections/{connection.Collection}", Body(new { vectors = new { size = records[0].Vector.Length, distance = "Cosine" } }), cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(created, "create collection", cancellationToken).ConfigureAwait(false);
            size = records[0].Vector.Length;
        }

        foreach (var record in records)
            Check(connection, size.Value, record.Vector.Length);

        var points = records.Select(r =>
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { [TextField] = r.Text, [IdField] = r.Id };
            foreach (var (key, value) in r.Metadata ?? new Dictionary<string, string>())
                payload[key] = value;
            return new { id = PointId(r.Id), vector = r.Vector, payload };
        });
        var response = await connection.Client.PutAsync($"{connection.BaseUrl}/collections/{connection.Collection}/points?wait=true", Body(new { points }), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "insert points", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] query, int limit, IReadOnlyDictionary<string, string>? filter = null, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectAsync(binding, cancellationToken).ConfigureAwait(false);
        if (await SizeAsync(connection, cancellationToken).ConfigureAwait(false) is not { } size)
            return []; // nothing inserted yet
        Check(connection, size, query.Length);

        object body = filter is { Count: > 0 }
            ? new { vector = query, limit, with_payload = true, filter = new { must = filter.Select(f => new { key = f.Key, match = new { value = f.Value } }).ToArray() } }
            : new { vector = query, limit, with_payload = true };
        var response = await connection.Client.PostAsync($"{connection.BaseUrl}/collections/{connection.Collection}/points/search", Body(body), cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await EnsureSuccessAsync(response, "search points", cancellationToken).ConfigureAwait(false));

        var matches = new List<VectorMatch>();
        if (!document.RootElement.TryGetProperty("result", out var results))
            return matches;
        foreach (var item in results.EnumerateArray())
        {
            var id = item.GetProperty("id").ToString();
            var score = item.TryGetProperty("score", out var scoreElement) ? scoreElement.GetSingle() : 0f;
            var text = string.Empty;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in payload.EnumerateObject())
                {
                    var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
                    if (property.Name == TextField)
                        text = value;
                    else if (property.Name == IdField)
                        id = value;
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
        if (await SizeAsync(connection, cancellationToken).ConfigureAwait(false) is null)
            return;
        var response = await connection.Client.PostAsync($"{connection.BaseUrl}/collections/{connection.Collection}/points/delete?wait=true", Body(new { points = ids.Select(PointId).ToArray() }), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "delete points", cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectAsync(binding, cancellationToken).ConfigureAwait(false);
        if (await SizeAsync(connection, cancellationToken).ConfigureAwait(false) is null)
            return;
        var response = await connection.Client.DeleteAsync($"{connection.BaseUrl}/collections/{connection.Collection}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "remove collection", cancellationToken).ConfigureAwait(false);
    }

    // ── Connection ───────────────────────────────────────────────────────

    private sealed record Connection(HttpClient Client, string BaseUrl, string Collection);

    private async Task<Connection> ConnectAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        var credentialsId = binding.RequireString("qdrant_credentials");
        var credentials = await _credentialsService.GetByIdAsync(credentialsId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Credentials '{credentialsId}' not found.");
        var baseUrl = credentials.GetField("base_url")?.TrimEnd('/') ?? throw new InvalidOperationException("Qdrant credentials missing 'base_url' field.");
        var apiKey = credentials.GetField("api_key");
        var collection = binding.GetString("collection_name") is { Length: > 0 } named ? named.Trim() : "agentivity";

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Remove("api-key");
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Add("api-key", apiKey);
        return new Connection(client, baseUrl, collection);
    }

    /// <summary>The size of the vectors of the collection, or null when it does not exist (nothing inserted yet).</summary>
    private async Task<int?> SizeAsync(Connection connection, CancellationToken cancellationToken)
    {
        var response = await connection.Client.GetAsync($"{connection.BaseUrl}/collections/{connection.Collection}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        using var document = JsonDocument.Parse(await EnsureSuccessAsync(response, "read collection", cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("result", out var result)
            && result.TryGetProperty("config", out var config)
            && config.TryGetProperty("params", out var parameters)
            && parameters.TryGetProperty("vectors", out var vectors)
            && vectors.TryGetProperty("size", out var size)
            ? size.GetInt32()
            : null;
    }

    private static void Check(Connection connection, int holds, int given)
    {
        if (holds != given)
            throw new VectorSizeMismatchException($"The Qdrant collection '{connection.Collection}'", holds, given);
    }

    private static StringContent Body(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    // A Qdrant point id is a number or a UUID: any other id (a product code, a hash) becomes a stable UUID, the same each time, so
    // inserting it again replaces the point.
    private static string PointId(string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString() : new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(id))).ToString();

    private async Task<string> EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Qdrant {Operation} failed. Status: {StatusCode}, Response: {Response}", operation, (int)response.StatusCode, body);
            throw new InvalidOperationException($"Qdrant {operation} failed with {response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }

        return body;
    }
}
