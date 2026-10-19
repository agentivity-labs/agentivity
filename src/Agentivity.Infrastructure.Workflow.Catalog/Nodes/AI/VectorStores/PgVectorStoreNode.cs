using Agentivity.Domain.Workflow;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;
using Npgsql;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.VectorStores;

/// <summary>
/// pgvector Vector Store — same role as <see cref="QdrantVectorStoreNode"/>, in a PostgreSQL table with the pgvector extension, for
/// teams that already run Postgres. The table is the collection, created at the first insert with the size of the vectors of the
/// Embeddings connected to the store; emptying it removes it, so it can be filled again with another model. The connection is a
/// plain connection string, which carries its own authentication.
/// </summary>
public sealed partial class PgVectorStoreNode : ResourceNode, IVectorStoreProvider
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ValidTableName();

    public PgVectorStoreNode()
        : base("ai.vectorstore.pgvector")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.VectorStore);

        DefineMetadata("pgvector Vector Store", "Texts kept with their vectors in PostgreSQL (pgvector extension), and found again by meaning.", "ai.vectorstore", whenToUse: "To keep texts in your PostgreSQL database (with the pgvector extension) and find them again by meaning, from one run to the next. Needs a PostgreSQL connection string; the table is created at the first insert. For what an agent should know (documents, pages, feeds), use a Knowledge Base instead.");
        DefineEffects(NodeEffects.ReadsExternal | NodeEffects.WritesExternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Vector Store", ResourcePortCategory.VectorStore, maxConnections: -1);
        DefineResourceInputPort("embeddings", "Embeddings", ResourcePortCategory.Embeddings, "The model that makes the vectors (optional; the free Local Embeddings model when nothing is connected). Keep the same one for a table.", isRequired: false);

        DefineInput(
            "connection_string",
            ArgumentType.Text,
            "PostgreSQL connection string (the database must have the pgvector extension available).",
            ui: new ArgumentUiMetadata
            {
                Group = "Connection",
                Order = 10,
                Multiline = true,
                Rows = 4,
            },
            required: true
        );
        DefineInput("table_name", ArgumentType.Text, "Table name (created at the first insert). Letters, digits, underscore only.", "agentivity_vectors", ui: new ArgumentUiMetadata { Group = "Collection", Order = 20 }, required: false);

        DefineIcon(NodeIcon.Embedded("aimemory"));
    }

    public async Task UpsertAsync(ResourceBinding binding, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
            return;
        var (connection, table) = await OpenAsync(binding, cancellationToken).ConfigureAwait(false);
        await using var _ = connection;
        var size = await SizeAsync(connection, table, cancellationToken).ConfigureAwait(false);
        if (size is null)
        {
            await ExecuteAsync(connection, null, "CREATE EXTENSION IF NOT EXISTS vector", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, $"CREATE TABLE IF NOT EXISTS {table} (id TEXT PRIMARY KEY, doc_text TEXT NOT NULL, metadata JSONB, embedding vector({records[0].Vector.Length.ToString(CultureInfo.InvariantCulture)}))", cancellationToken).ConfigureAwait(false);
            size = records[0].Vector.Length;
        }

        foreach (var record in records)
            Check(table, size.Value, record.Vector.Length);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            await using var command = new NpgsqlCommand($"INSERT INTO {table} (id, doc_text, metadata, embedding) VALUES ($1, $2, $3::jsonb, $4::vector) ON CONFLICT (id) DO UPDATE SET doc_text = EXCLUDED.doc_text, metadata = EXCLUDED.metadata, embedding = EXCLUDED.embedding", connection, transaction);
            command.Parameters.AddWithValue(record.Id);
            command.Parameters.AddWithValue(record.Text);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(record.Metadata ?? new Dictionary<string, string>()));
            command.Parameters.AddWithValue(Literal(record.Vector));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] query, int limit, IReadOnlyDictionary<string, string>? filter = null, CancellationToken cancellationToken = default)
    {
        var (connection, table) = await OpenAsync(binding, cancellationToken).ConfigureAwait(false);
        await using var _ = connection;
        if (await SizeAsync(connection, table, cancellationToken).ConfigureAwait(false) is not { } size)
            return []; // nothing inserted yet
        Check(table, size, query.Length);

        // The metadata filter: every value it gives must be in the record's metadata (JSONB containment).
        var where = filter is { Count: > 0 } ? "WHERE metadata @> $3::jsonb" : string.Empty;
        await using var command = new NpgsqlCommand($"SELECT id, doc_text, metadata, 1 - (embedding <=> $1::vector) AS score FROM {table} {where} ORDER BY embedding <=> $1::vector LIMIT $2", connection);
        command.Parameters.AddWithValue(Literal(query));
        command.Parameters.AddWithValue(limit);
        if (filter is { Count: > 0 })
            command.Parameters.AddWithValue(JsonSerializer.Serialize(filter));

        var matches = new List<VectorMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var metadata = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2));
            matches.Add(new VectorMatch(reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1), (float)reader.GetDouble(3), metadata is { Count: > 0 } ? metadata : null));
        }

        return matches;
    }

    public async Task DeleteAsync(ResourceBinding binding, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return;
        var (connection, table) = await OpenAsync(binding, cancellationToken).ConfigureAwait(false);
        await using var _ = connection;
        if (await SizeAsync(connection, table, cancellationToken).ConfigureAwait(false) is null)
            return;
        await using var command = new NpgsqlCommand($"DELETE FROM {table} WHERE id = ANY($1)", connection);
        command.Parameters.AddWithValue(ids.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        var (connection, table) = await OpenAsync(binding, cancellationToken).ConfigureAwait(false);
        await using var _ = connection;
        await ExecuteAsync(connection, null, $"DROP TABLE IF EXISTS {table}", cancellationToken).ConfigureAwait(false);
    }

    // ── The table ────────────────────────────────────────────────────────

    /// <summary>The table name, checked: it is written into the SQL, so only letters, digits and underscores are accepted.</summary>
    internal static string TableOf(ResourceBinding binding)
    {
        var table = binding.GetString("table_name") is { Length: > 0 } named ? named.Trim() : "agentivity_vectors";
        return ValidTableName().IsMatch(table) ? table : throw new InvalidOperationException($"Invalid pgvector table name '{table}' — only letters, digits, and underscores are allowed.");
    }

    private static async Task<(NpgsqlConnection Connection, string Table)> OpenAsync(ResourceBinding binding, CancellationToken cancellationToken)
    {
        var table = TableOf(binding);
        var connection = new NpgsqlConnection(binding.RequireString("connection_string"));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (connection, table);
    }

    /// <summary>The vector size of the table's embedding column (pgvector keeps it as the column's type modifier), or null when there is no table yet.</summary>
    private static async Task<int?> SizeAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT a.atttypmod FROM pg_attribute a WHERE a.attrelid = to_regclass($1) AND a.attname = 'embedding' AND NOT a.attisdropped", connection);
        command.Parameters.AddWithValue(table);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int size ? size : null;
    }

    private static void Check(string table, int holds, int given)
    {
        if (holds != given)
            throw new VectorSizeMismatchException($"The pgvector table '{table}'", holds, given);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Literal(float[] vector)
    {
        var builder = new StringBuilder(vector.Length * 8).Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(vector[i].ToString(CultureInfo.InvariantCulture));
        }

        return builder.Append(']').ToString();
    }
}
