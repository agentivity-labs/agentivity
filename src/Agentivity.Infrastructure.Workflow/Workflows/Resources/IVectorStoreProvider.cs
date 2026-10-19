namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>A text kept in a vector store: its id, its text, its vector, and what is said of it.</summary>
public sealed record VectorRecord(string Id, string Text, float[] Vector, IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>A text found by a search, the closest first: its id, its text, how close it is (the higher, the closer), what is said of it.</summary>
public sealed record VectorMatch(string Id, string Text, float Score, IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// A collection holds vectors of one size, that of the Embeddings model it was filled with: a vector of another size (another
/// model) is refused, saying so, instead of failing deep in the database.
/// </summary>
public sealed class VectorSizeMismatchException(string where, int holds, int given)
    : InvalidOperationException($"{where} holds vectors of {holds} numbers, and the Embeddings model gives {given}: use the same Embeddings model as when its texts were inserted, or another collection.");

/// <summary>
/// A vector store (In-Memory, Qdrant, Pinecone, pgvector): texts kept with their vectors, in a collection, found again by meaning.
/// The node is the client — consumers resolve it and call it, as for <see cref="IEmbeddingProvider"/>. The vectors are made by
/// whoever calls it, with the Embeddings connected to the store (see <c>VectorStoreOperations</c>). Same operations as the
/// standard ones (LangChain, n8n): insert by id (inserting an id again replaces it), search with a limit and a filter on the
/// metadata, delete by id, clear.
/// </summary>
public interface IVectorStoreProvider
{
    /// <summary>Inserts the records; one whose id is already there replaces it.</summary>
    Task UpsertAsync(ResourceBinding binding, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken = default);

    /// <summary>The <paramref name="limit"/> records closest to <paramref name="query"/>, among those whose metadata has every value of <paramref name="filter"/>.</summary>
    Task<IReadOnlyList<VectorMatch>> QueryAsync(ResourceBinding binding, float[] query, int limit, IReadOnlyDictionary<string, string>? filter = null, CancellationToken cancellationToken = default);

    /// <summary>Removes these records; an id that is not there is not an error.</summary>
    Task DeleteAsync(ResourceBinding binding, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Removes every record of the collection.</summary>
    Task ClearAsync(ResourceBinding binding, CancellationToken cancellationToken = default);
}
