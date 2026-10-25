using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentivity.Infrastructure.KnowledgeBases.Engine;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Services.AI;

/// <summary>A text to insert in a vector store, with what is said of it, and its id when it has one.</summary>
public sealed record VectorDocument(string Text, IReadOnlyDictionary<string, string>? Metadata = null, string? Id = null);

/// <summary>
/// What is done with a vector store, the same way from a workflow step and from an agent's tool: the vectors are made with the
/// Embeddings connected to the store (or the free local model), then the store is asked. A text without an id gets one made from
/// its text and its metadata, so inserting the same text twice does not keep it twice.
/// </summary>
public static class VectorStoreOperations
{
    public static string IdOf(string text, IReadOnlyDictionary<string, string>? metadata)
    {
        var builder = new StringBuilder(text);
        foreach (var (key, value) in (metadata ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal))
            builder.Append('\n').Append(key).Append('=').Append(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32].ToLowerInvariant();
    }

    /// <summary>Inserts the texts (empty ones are left out), after emptying the collection when asked; gives their ids.</summary>
    public static async Task<IReadOnlyList<string>> InsertAsync(IVectorStoreProvider store, ResourceBinding binding, IEmbeddingProvider localEmbeddings, IReadOnlyList<VectorDocument> documents, bool clearFirst, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(documents);
        var kept = documents.Where(d => !string.IsNullOrWhiteSpace(d.Text)).ToList();
        if (clearFirst)
            await store.ClearAsync(binding, cancellationToken).ConfigureAwait(false);
        if (kept.Count == 0)
            return [];

        var (embedder, embedderBinding) = EmbeddingProviderResolver.Resolve(binding, "embeddings", localEmbeddings);
        var vectors = await embedder.CreateBatchEmbeddingsAsync(embedderBinding, kept.Select(d => d.Text), cancellationToken).ConfigureAwait(false);
        var records = kept.Select((d, i) => new VectorRecord(string.IsNullOrWhiteSpace(d.Id) ? IdOf(d.Text, d.Metadata) : d.Id.Trim(), d.Text, vectors[i], d.Metadata)).ToList();
        await store.UpsertAsync(binding, records, cancellationToken).ConfigureAwait(false);
        return [.. records.Select(r => r.Id)];
    }

    /// <summary>The texts closest to the question, among those whose metadata has every value of the filter.</summary>
    public static async Task<IReadOnlyList<VectorMatch>> SearchAsync(IVectorStoreProvider store, ResourceBinding binding, IEmbeddingProvider localEmbeddings, string query, int limit, IReadOnlyDictionary<string, string>? filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var (embedder, embedderBinding) = EmbeddingProviderResolver.Resolve(binding, "embeddings", localEmbeddings);
        var vector = await embedder.CreateEmbeddingAsync(embedderBinding, query, cancellationToken).ConfigureAwait(false);
        return await store.QueryAsync(binding, vector, Math.Max(1, limit), filter is { Count: > 0 } ? filter : null, cancellationToken).ConfigureAwait(false);
    }

    // ── What a step or a tool is given ───────────────────────────────────

    /// <summary>
    /// The texts given to insert: one text, a JSON list of texts, or a JSON list of <c>{ "text", "metadata", "id" }</c> (a single such
    /// object too). <paramref name="metadata"/> is said of every text, under what a text says of itself.
    /// </summary>
    public static IReadOnlyList<VectorDocument> ReadDocuments(string? given, IReadOnlyDictionary<string, string>? metadata)
    {
        var text = given?.Trim();
        if (string.IsNullOrEmpty(text))
            return [];
        if (text[0] is '[' or '{')
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                var items = json.RootElement.ValueKind == JsonValueKind.Array ? [.. json.RootElement.EnumerateArray()] : new[] { json.RootElement };
                return [.. items.Select(item => Document(item, metadata)).Where(d => d is not null)!];
            }
            catch (JsonException)
            {
                // Not JSON after all: the text itself.
            }
        }

        return [new VectorDocument(text, metadata)];
    }

    private static VectorDocument? Document(JsonElement item, IReadOnlyDictionary<string, string>? common)
    {
        if (item.ValueKind == JsonValueKind.String)
            return new VectorDocument(item.GetString() ?? "", common);
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            return null;
        var metadata = new Dictionary<string, string>(common ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (item.TryGetProperty("metadata", out var own) && own.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in own.EnumerateObject())
                metadata[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
        }

        var id = item.TryGetProperty("id", out var given) && given.ValueKind is JsonValueKind.String or JsonValueKind.Number ? given.ToString() : null;
        return new VectorDocument(text.GetString() ?? "", metadata.Count == 0 ? null : metadata, id);
    }

    /// <summary>A JSON object of text values (metadata, a filter), or null when there is none.</summary>
    public static IReadOnlyDictionary<string, string>? ReadValues(string? given, string what)
    {
        if (string.IsNullOrWhiteSpace(given))
            return null;
        try
        {
            using var json = JsonDocument.Parse(given);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{what} is a JSON object, such as {{ \"kind\": \"laptop\" }}.");
            var values = json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText(), StringComparer.Ordinal);
            return values.Count == 0 ? null : values;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{what} is a JSON object, such as {{ \"kind\": \"laptop\" }}.");
        }
    }

    /// <summary>Ids given as a JSON list, or separated by commas or new lines.</summary>
    public static IReadOnlyList<string> ReadIds(string? given)
    {
        var text = given?.Trim();
        if (string.IsNullOrEmpty(text))
            return [];
        if (text.StartsWith('['))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                return [.. json.RootElement.EnumerateArray().Select(e => e.ToString().Trim()).Where(e => e.Length > 0)];
            }
            catch (JsonException)
            {
                // Read as a list of words below.
            }
        }

        return [.. text.Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Each long text split into passages (paragraphs, then sentences), each keeping what is said of the text and its place.</summary>
    public static IReadOnlyList<VectorDocument> SplitLongTexts(IReadOnlyList<VectorDocument> documents) =>
    [
        .. documents.SelectMany(document =>
        {
            var passages = TextChunker.Split(document.Text, 900, 100, TextSplitStrategy.RecursiveCharacter);
            if (passages.Count <= 1)
                return [document];
            var baseId = string.IsNullOrWhiteSpace(document.Id) ? IdOf(document.Text, document.Metadata) : document.Id.Trim();
            return passages.Select((passage, i) => new VectorDocument(passage, document.Metadata, $"{baseId}#{i + 1}"));
        }),
    ];
}
