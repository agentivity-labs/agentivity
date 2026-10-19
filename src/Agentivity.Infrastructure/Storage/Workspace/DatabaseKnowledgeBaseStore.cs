using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The Knowledge Bases in the workspace's database, the same way whatever the database (see <see cref="IKnowledgeBaseStore"/>): the
/// tables <c>knowledge_documents</c>, <c>knowledge_passages</c> and <c>knowledge_states</c>, made with the rest of the workspace by
/// the module that keeps it. A vector is kept as its numbers, 4 bytes each. A search reads the passages of the base one at a time
/// and keeps the closest: nothing is loaded whole, and it is fast enough for a hundred thousand passages.
/// </summary>
public abstract class DatabaseKnowledgeBaseStore : IKnowledgeBaseStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A connection to the database, its tables made.</summary>
    protected abstract Task<DbConnection> OpenAsync(CancellationToken cancellationToken);

    public async ValueTask<IReadOnlyDictionary<string, string>> FingerprintsAsync(string baseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, "SELECT doc_key, fingerprint FROM knowledge_documents WHERE base_id = @base", ("base", baseId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            found[reader.GetString(0)] = reader.GetString(1);
        return found;
    }

    public async ValueTask WriteDocumentAsync(string baseId, KnowledgeBaseDocumentRecord document, IReadOnlyList<KnowledgeBasePassageRecord> passages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(passages);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RemoveAsync(connection, transaction, baseId, document.Key, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO knowledge_documents (base_id, doc_key, fingerprint, title, source, synced_at) VALUES (@base, @key, @fingerprint, @title, @source, @synced)",
                cancellationToken,
                ("base", baseId),
                ("key", document.Key),
                ("fingerprint", document.Fingerprint),
                ("title", document.Title),
                ("source", document.Source),
                ("synced", Date(document.SyncedAt))
            )
            .ConfigureAwait(false);
        foreach (var passage in passages)
        {
            await ExecuteAsync(
                    connection,
                    transaction,
                    "INSERT INTO knowledge_passages (base_id, doc_key, ordinal, text, vector, metadata) VALUES (@base, @key, @ordinal, @text, @vector, @metadata)",
                    cancellationToken,
                    ("base", baseId),
                    ("key", document.Key),
                    ("ordinal", passage.Ordinal),
                    ("text", passage.Text),
                    ("vector", Bytes(passage.Vector)),
                    ("metadata", passage.Metadata is { Count: > 0 } metadata ? JsonSerializer.Serialize(metadata, Json) : null)
                )
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveDocumentsAsync(string baseId, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            return;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
            await RemoveAsync(connection, transaction, baseId, key, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EmptyAsync(string baseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in new[] { "knowledge_passages", "knowledge_documents", "knowledge_states" })
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE base_id = @base", cancellationToken, ("base", baseId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<KnowledgeBaseCounts> CountAsync(string baseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            null,
            "SELECT (SELECT COUNT(*) FROM knowledge_documents WHERE base_id = @base), (SELECT COUNT(*) FROM knowledge_passages WHERE base_id = @base)",
            ("base", baseId)
        );
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new KnowledgeBaseCounts(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    public async ValueTask<IReadOnlyList<KnowledgeBaseDocumentSummary>> ListDocumentsAsync(string baseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(
            connection,
            null,
            "SELECT d.doc_key, d.title, d.source, d.synced_at, (SELECT COUNT(*) FROM knowledge_passages p WHERE p.base_id = d.base_id AND p.doc_key = d.doc_key) FROM knowledge_documents d WHERE d.base_id = @base",
            ("base", baseId)
        );
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var documents = new List<KnowledgeBaseDocumentSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(
                new KnowledgeBaseDocumentSummary(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture)
                )
            );
        }

        return [.. documents.OrderBy(d => d.Title ?? d.Key, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async ValueTask<IReadOnlyList<KnowledgeBaseMatch>> SearchAsync(string baseId, float[] query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (limit <= 0)
            return [];

        var queryNorm = Norm(query);
        // The closest so far, the least close first: a passage closer than it takes its place.
        var closest = new PriorityQueue<(string Key, int Ordinal, string Text, string? Metadata), double>();
        await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = Command(connection, null, "SELECT doc_key, ordinal, text, vector, metadata FROM knowledge_passages WHERE base_id = @base", ("base", baseId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var score = Cosine(query, queryNorm, (byte[])reader.GetValue(3));
                if (closest.Count == limit && closest.TryPeek(out _, out var least) && score <= least)
                    continue;
                if (closest.Count == limit)
                    closest.Dequeue();
                closest.Enqueue((reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), reader.GetString(2), reader.IsDBNull(4) ? null : reader.GetString(4)), score);
            }
        }

        var found = new List<(string Key, int Ordinal, string Text, string? Metadata, double Score)>();
        while (closest.TryDequeue(out var passage, out var score))
            found.Add((passage.Key, passage.Ordinal, passage.Text, passage.Metadata, score));
        found.Reverse();

        var documents = await DocumentsAsync(baseId, found.Select(f => f.Key).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        return
        [
            .. found.Select(f =>
            {
                documents.TryGetValue(f.Key, out var document);
                var metadata = f.Metadata is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(f.Metadata, Json);
                return new KnowledgeBaseMatch(f.Key, f.Ordinal, f.Text, document.Title, document.Source, f.Score, metadata);
            }),
        ];
    }

    public async ValueTask SaveStateAsync(string baseId, KnowledgeBaseSyncState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_states WHERE base_id = @base", cancellationToken, ("base", baseId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "INSERT INTO knowledge_states (base_id, state) VALUES (@base, @state)", cancellationToken, ("base", baseId), ("state", JsonSerializer.Serialize(state, Json))).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<KnowledgeBaseSyncState?> StateAsync(string baseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, "SELECT state FROM knowledge_states WHERE base_id = @base", ("base", baseId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string text ? JsonSerializer.Deserialize<KnowledgeBaseSyncState>(text, Json) : null;
    }

    // ── What a passage is made of ────────────────────────────────────────

    private async Task<Dictionary<string, (string? Title, string? Source)>> DocumentsAsync(string baseId, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, (string? Title, string? Source)>(StringComparer.Ordinal);
        if (keys.Count == 0)
            return found;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            await using var command = Command(connection, null, "SELECT title, source FROM knowledge_documents WHERE base_id = @base AND doc_key = @key", ("base", baseId), ("key", key));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                found[key] = (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        return found;
    }

    private static async Task RemoveAsync(DbConnection connection, DbTransaction transaction, string baseId, string key, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_passages WHERE base_id = @base AND doc_key = @key", cancellationToken, ("base", baseId), ("key", key)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_documents WHERE base_id = @base AND doc_key = @key", cancellationToken, ("base", baseId), ("key", key)).ConfigureAwait(false);
    }

    private static byte[] Bytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static double Norm(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
            sum += value * value;
        return Math.Sqrt(sum);
    }

    private static double Cosine(float[] query, double queryNorm, byte[] stored)
    {
        var length = Math.Min(query.Length, stored.Length / sizeof(float));
        double dot = 0;
        double norm = 0;
        for (var i = 0; i < length; i++)
        {
            var value = BitConverter.ToSingle(stored, i * sizeof(float));
            dot += query[i] * value;
            norm += value * value;
        }

        return queryNorm == 0 || norm == 0 ? 0 : dot / (queryNorm * Math.Sqrt(norm));
    }

    private static string Date(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    // ── Commands ─────────────────────────────────────────────────────────

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
