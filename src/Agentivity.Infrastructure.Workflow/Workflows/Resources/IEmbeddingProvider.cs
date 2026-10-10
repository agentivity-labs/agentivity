namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Interface for embedding resource nodes (LLM Memory nodes).
/// The node is the client — consumers resolve the node and call it directly.
/// No factory or capability registry involved.
/// </summary>
public interface IEmbeddingProvider
{
    Task<float[]> CreateEmbeddingAsync(ResourceBinding binding, string text, CancellationToken cancellationToken = default);

    Task<List<float[]>> CreateBatchEmbeddingsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken = default);

    int GetEmbeddingDimensions(ResourceBinding binding);

    string GetModelName(ResourceBinding binding);
}
