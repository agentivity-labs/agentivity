namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Interface for vector-store resource nodes (real ANN backends: Pinecone, Qdrant, pgvector…).
/// Same convention as <see cref="IEmbeddingProvider"/> — the node IS the client, no factory or
/// registry involved. A vector-store node also implements <see cref="IEmbeddingProvider"/> itself,
/// delegating to whatever is connected on its "embeddings" input port, so existing consumers that
/// only know about <see cref="IEmbeddingProvider"/> keep working unchanged; a consumer that wants
/// real persistence and ANN search checks for this interface instead of embedding+ranking locally.
/// </summary>
public interface IVectorStoreProvider
{
    Task UpsertAsync(ResourceBinding binding, string id, float[] embedding, string text, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] queryEmbedding, int topK, CancellationToken cancellationToken = default);

    Task DeleteAsync(ResourceBinding binding, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of <paramref name="ids"/> that already exist in the store. Callers use
    /// this to skip re-embedding (and re-upserting) content that hasn't changed since a previous
    /// run — ids are expected to be deterministic content hashes, so an existing id means the
    /// content is byte-identical to what's already indexed.
    /// </summary>
    Task<IReadOnlySet<string>> GetExistingIdsAsync(ResourceBinding binding, IReadOnlyList<string> ids, CancellationToken cancellationToken = default);
}

public sealed record VectorMatch(string Id, string Text, float Score, IReadOnlyDictionary<string, string>? Metadata);
