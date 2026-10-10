namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Shared 3-tier fallback used by every memory/vector-store node's optional "embeddings" input
/// port: (1) an externally connected Embeddings resource node, if wired — lets a user swap the
/// embedding provider independently of the storage backend; (2) the node's own bundled
/// credentials, if configured — preserves existing workflows unchanged; (3) a local fallback
/// (typically the shared ONNX model) — so a memory node works out of the box with zero
/// configuration. See EPIC-0462 P0-2 for why this stays optional rather than a required port like
/// n8n's Vector Store nodes: the common case (one storage backend, one embedding, never mixed)
/// should never need two nodes wired together.
/// </summary>
public static class EmbeddingProviderResolver
{
    public static (IEmbeddingProvider Provider, ResourceBinding Binding) Resolve(ResourceBinding ownBinding, string embeddingsPortName, IEmbeddingProvider? bundled, IEmbeddingProvider localFallback)
    {
        ArgumentNullException.ThrowIfNull(ownBinding);
        ArgumentNullException.ThrowIfNull(localFallback);

        var external = ResourceResolver.TryResolveNestedToolNode<IEmbeddingProvider>(ownBinding, embeddingsPortName, ResourcePortCategory.Embeddings);
        if (external is not null)
        {
            return external.Value;
        }

        if (bundled is not null)
        {
            return (bundled, ownBinding);
        }

        return (localFallback, ownBinding);
    }
}
