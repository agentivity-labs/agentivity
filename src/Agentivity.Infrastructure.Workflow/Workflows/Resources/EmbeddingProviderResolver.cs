namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// The Embeddings of a node that has an "embeddings" port (a vector store): the Embeddings node connected there, or — nothing
/// connected — the local fallback (the shared free model), so a vector store works with no setting at all. One way only, as in
/// n8n where a vector store always takes its Embeddings sub-node.
/// </summary>
public static class EmbeddingProviderResolver
{
    public static (IEmbeddingProvider Provider, ResourceBinding Binding) Resolve(ResourceBinding ownBinding, string embeddingsPortName, IEmbeddingProvider localFallback)
    {
        ArgumentNullException.ThrowIfNull(ownBinding);
        ArgumentNullException.ThrowIfNull(localFallback);

        return ResourceResolver.TryResolveNestedToolNode<IEmbeddingProvider>(ownBinding, embeddingsPortName, ResourcePortCategory.Embeddings) ?? (localFallback, ownBinding);
    }
}
