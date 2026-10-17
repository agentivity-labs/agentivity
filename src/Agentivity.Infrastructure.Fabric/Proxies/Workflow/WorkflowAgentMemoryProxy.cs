using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Workflow implementation of <see cref="IAgentMemoryProxy"/>.
/// Resolves the <c>llm_memory</c> embedding provider and <c>knowledge</c> source nodes from the
/// node context and runs the same embedding + cosine-ranking pipeline as the standalone path
/// (<c>AgentMemoryResolver</c>) — unless the resolved memory node is also a real
/// <see cref="IVectorStoreProvider"/> (e.g. Qdrant), in which case knowledge documents are
/// persisted once (idempotent, keyed by a content hash, and skipped entirely once already
/// indexed) and the query goes through the vector store's own ANN index instead of a local
/// brute-force cosine scan — see <see cref="AgentMemoryVectorStoreRetrieval"/>.
/// </summary>
public sealed class WorkflowAgentMemoryProxy(NodeConfig config, WorkflowRunServices services) : IAgentMemoryProxy
{
    public async ValueTask<string> RetrieveAsync(string query, int topK, CancellationToken ct)
    {
        var resolvedMemory = ResourceResolver.TryResolveToolNode<IEmbeddingProvider>(config, "llm_memory", ResourcePortCategory.LlmMemory);
        if (resolvedMemory is null)
        {
            return string.Empty;
        }

        try
        {
            var knowledge = await KnowledgeSourceResolver.ResolveDocumentsAsync(config, portName: "knowledge", resourceType: ResourcePortCategory.KnowledgeSource, ct).ConfigureAwait(false);
            if (knowledge.Count == 0)
            {
                return string.Empty;
            }

            services.PublishStatus($"Retrieving memory config from {knowledge.Count} knowledge item(s)...");

            var queryEmbedding = await resolvedMemory.Value.Tool.CreateEmbeddingAsync(resolvedMemory.Value.Binding, query, ct).ConfigureAwait(false);

            if (resolvedMemory.Value.Tool is IVectorStoreProvider vectorStore)
            {
                return await AgentMemoryVectorStoreRetrieval.RetrieveAsync(vectorStore, resolvedMemory.Value.Tool, resolvedMemory.Value.Binding, knowledge, queryEmbedding, topK, ct).ConfigureAwait(false);
            }

            var itemEmbeddings = await resolvedMemory.Value.Tool.CreateBatchEmbeddingsAsync(resolvedMemory.Value.Binding, knowledge.Select(item => item.Text), ct).ConfigureAwait(false);

            var topMatches = KnowledgeRetrieval.RankTopMatches(queryEmbedding, knowledge, itemEmbeddings, topK);
            return KnowledgeRetrieval.FormatContext(topMatches, "Knowledge");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Knowledge was connected to be used: answering without it, as if there were none, would hide the failure.
            throw new InvalidOperationException($"The connected knowledge could not be searched: {ex.Message}", ex);
        }
    }
}
