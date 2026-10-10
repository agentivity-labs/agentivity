namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Wraps a memory node's own "bundled credentials" embedding logic as an
/// <see cref="IEmbeddingProvider"/>, so <see cref="EmbeddingProviderResolver"/> can treat it
/// uniformly alongside an externally-connected provider or the local fallback — without each node
/// needing its own private adapter class just to avoid recursing back through its own public
/// (now resolver-routed) methods.
/// </summary>
public sealed class DelegatingEmbeddingProvider(
    Func<ResourceBinding, string, CancellationToken, Task<float[]>> createEmbedding,
    Func<ResourceBinding, IEnumerable<string>, CancellationToken, Task<List<float[]>>> createBatchEmbeddings,
    Func<ResourceBinding, int> getEmbeddingDimensions,
    Func<ResourceBinding, string> getModelName
) : IEmbeddingProvider
{
    public Task<float[]> CreateEmbeddingAsync(ResourceBinding binding, string text, CancellationToken cancellationToken = default) => createEmbedding(binding, text, cancellationToken);

    public Task<List<float[]>> CreateBatchEmbeddingsAsync(ResourceBinding binding, IEnumerable<string> texts, CancellationToken cancellationToken = default) => createBatchEmbeddings(binding, texts, cancellationToken);

    public int GetEmbeddingDimensions(ResourceBinding binding) => getEmbeddingDimensions(binding);

    public string GetModelName(ResourceBinding binding) => getModelName(binding);
}
