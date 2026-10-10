using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models.Capabilities;
using Agentivity.Infrastructure.Workflow.Catalog.Services.AI;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Memory;

/// <summary>
/// pgvector Memory Node — a real, persistent vector store backed by PostgreSQL's pgvector
/// extension, same role as <see cref="LlmMemoryQdrantNode"/>/<see cref="LlmMemoryPineconeNode"/>
/// for teams that already run Postgres and would rather not stand up a dedicated vector database.
/// Falls back to the free shared local embedding when no embedding credentials are set, same
/// 3-tier resolution as every other memory node (see <see cref="EmbeddingProviderResolver"/>).
/// Connection is a plain connection string, same convention as <c>AIKnowledgePostgresNode</c> —
/// not a credentials-store template, since a Postgres connection string already carries its own
/// auth.
/// </summary>
public sealed class LlmMemoryPgVectorNode : ResourceNode, IEmbeddingProvider, IVectorStoreProvider
{
    private static readonly Regex ValidTableName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly CredentialsService _credentialsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlmMemoryPgVectorNode> _logger;
    private readonly LocalEmbeddingService _local;

    public LlmMemoryPgVectorNode(CredentialsService credentialsService, IHttpClientFactory httpClientFactory, LocalEmbeddingService local, ILogger<LlmMemoryPgVectorNode> logger)
        : base("ai.memory.pgvector")
    {
        _credentialsService = credentialsService ?? throw new ArgumentNullException(nameof(credentialsService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.LlmMemory);

        DefineMetadata("pgvector Memory", "RAG backed by PostgreSQL's pgvector extension — persists embeddings once and queries a real ANN-ready index instead of re-ranking locally on every run.", "ai.memory");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "LLM Memory", ResourcePortCategory.LlmMemory, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "Optional — overrides the credentials below with a connected Embeddings node.", isRequired: false);

        DefineCredentialsInput("embedding_credentials", "openai", "OpenAI credentials used to embed text — leave unset to fall back to the free local embedding", ui: new ArgumentUiMetadata { Group = "Connection", Order = 10 });
        DefineInput(
            "connection_string",
            ArgumentType.Text,
            "PostgreSQL connection string (must have the pgvector extension available).",
            ui: new ArgumentUiMetadata
            {
                Group = "Connection",
                Order = 20,
                Multiline = true,
                Rows = 4,
            }
        );

        DefineChoice(
            "embedding_model",
            [("text-embedding-3-small", "text-embedding-3-small (Fast, Cheap)"), ("text-embedding-3-large", "text-embedding-3-large (Best Quality)"), ("text-embedding-ada-002", "text-embedding-ada-002 (Legacy)")],
            "Embedding model",
            "text-embedding-3-small",
            ui: new ArgumentUiMetadata { Group = "Settings", Order = 30 }
        );
        DefineInput("table_name", ArgumentType.Text, "Table name (created automatically on first use). Letters, digits, underscore only.", "agentivity_knowledge", ui: new ArgumentUiMetadata { Group = "Settings", Order = 40 });

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

    // ── IVectorStoreProvider — real pgvector persistence + cosine-distance search ───────────

    public async Task UpsertAsync(ResourceBinding binding, string id, float[] embedding, string text, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken = default)
    {
        (NpgsqlConnection connection, string table) = await PrepareTableAsync(binding, embedding.Length, cancellationToken).ConfigureAwait(false);
        await using var _ = connection;

        await using var command = new NpgsqlCommand($"INSERT INTO {table} (id, doc_text, metadata, embedding) VALUES ($1, $2, $3::jsonb, $4::vector) ON CONFLICT (id) DO UPDATE SET doc_text = EXCLUDED.doc_text, metadata = EXCLUDED.metadata, embedding = EXCLUDED.embedding", connection);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(text);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(metadata ?? new Dictionary<string, string>()));
        command.Parameters.AddWithValue(ToPgVectorLiteral(embedding));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        (NpgsqlConnection connection, string table) = await PrepareTableAsync(binding, queryEmbedding.Length, cancellationToken).ConfigureAwait(false);
        await using var _ = connection;

        await using var command = new NpgsqlCommand($"SELECT id, doc_text, metadata, 1 - (embedding <=> $1::vector) AS score FROM {table} ORDER BY embedding <=> $1::vector LIMIT $2", connection);
        command.Parameters.AddWithValue(ToPgVectorLiteral(queryEmbedding));
        command.Parameters.AddWithValue(topK);

        var matches = new List<VectorMatch>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string id = reader.GetString(0);
            string text = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            float score = reader.GetFloat(3);

            Dictionary<string, string>? metadata = null;
            if (!reader.IsDBNull(2))
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2));
            }

            matches.Add(new VectorMatch(id, text, score, metadata));
        }

        return matches;
    }

    public async Task DeleteAsync(ResourceBinding binding, string id, CancellationToken cancellationToken = default)
    {
        (NpgsqlConnection connection, string table) = await PrepareTableAsync(binding, vectorSize: null, cancellationToken).ConfigureAwait(false);
        await using var _ = connection;

        await using var command = new NpgsqlCommand($"DELETE FROM {table} WHERE id = $1", connection);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetExistingIdsAsync(ResourceBinding binding, IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return new HashSet<string>();
        }

        (NpgsqlConnection connection, string table) = await PrepareTableAsync(binding, vectorSize: null, cancellationToken).ConfigureAwait(false);
        await using var _ = connection;

        await using var command = new NpgsqlCommand($"SELECT id FROM {table} WHERE id = ANY($1)", connection);
        command.Parameters.AddWithValue(ids.ToArray());

        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            existing.Add(reader.GetString(0));
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

    /// <summary>
    /// Opens the connection and, when <paramref name="vectorSize"/> is supplied (upsert/query —
    /// the calls that actually need the table and its vector column to exist), ensures the
    /// pgvector extension and the table exist. Delete/exists-check calls pass null and skip
    /// creation — deleting from or checking a table that was never created is a correct no-op,
    /// not an error worth paying a CREATE TABLE round-trip for.
    /// </summary>
    private async Task<(NpgsqlConnection Connection, string Table)> PrepareTableAsync(ResourceBinding binding, int? vectorSize, CancellationToken cancellationToken)
    {
        string connectionString = binding.RequireString("connection_string");
        string table = binding.GetString("table_name") ?? "agentivity_knowledge";
        if (!ValidTableName.IsMatch(table))
        {
            throw new InvalidOperationException($"Invalid pgvector table name '{table}' — only letters, digits, and underscores are allowed.");
        }

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (vectorSize is { } dimensions)
        {
            await using (var extensionCommand = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", connection))
            {
                await extensionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var createCommand = new NpgsqlCommand($"CREATE TABLE IF NOT EXISTS {table} (id TEXT PRIMARY KEY, doc_text TEXT NOT NULL, metadata JSONB, embedding vector({dimensions.ToString(CultureInfo.InvariantCulture)}))", connection);
            await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return (connection, table);
    }

    private static string ToPgVectorLiteral(float[] embedding)
    {
        var builder = new StringBuilder(embedding.Length * 8);
        builder.Append('[');
        for (var i = 0; i < embedding.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(embedding[i].ToString(CultureInfo.InvariantCulture));
        }

        builder.Append(']');
        return builder.ToString();
    }
}
