namespace Agentivity.Infrastructure.Workflows.Resources.Capabilities;

/// <summary>
/// Capability for creating text embeddings.
/// Any RAG/Memory resource node can provide this capability.
///
/// External developers can create custom embedding providers by:
/// 1. Creating a resource node
/// 2. Implementing a factory that creates an IEmbeddingCapability
/// 3. Registering the factory in ResourceCapabilityRegistry
/// </summary>
public interface IEmbeddingCapability : IResourceCapability
{
    /// <summary>
    /// Creates an embedding vector for a single text
    /// </summary>
    Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates embedding vectors for multiple texts (batch operation for efficiency)
    /// </summary>
    Task<List<float[]>> CreateBatchEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the embedding dimensions
    /// </summary>
    int EmbeddingDimensions { get; }

    /// <summary>
    /// Gets the embedding model name/identifier
    /// </summary>
    string ModelName { get; }
}
