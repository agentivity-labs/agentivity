using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Core.Llm.Knowledge;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Canonical, agent-centric implementation of <see cref="IAgentMemoryProxy"/> (STORY-0459.6b).
/// <para>
/// Memory/RAG configuration belongs to the <see cref="AgentEntity"/> definition, not to any
/// particular execution shell. This resolver reads the <c>llm_memory</c> embedding provider and
/// <c>knowledge</c> source nodes directly from <see cref="AgentEntity.Graph"/> node definitions
/// via <see cref="NodeInstanceRegistry"/>, then executes the same embedding + cosine-ranking
/// pipeline as the workflow path (<c>WorkflowAgentMemoryProxy</c>).
/// </para>
/// <para>
/// Used today by the standalone path (<see cref="AgentExecutable.BuildStandaloneProxies"/>) and
/// intended for reuse by <c>AgentHost</c>/<c>AgentExecutable</c> generically once the unified
/// <c>ExecuteAsync</c> entry point lands (0459.6c/d) — it depends only on <see cref="AgentEntity"/>
/// and the shared resource-graph types, not on any workflow- or shell-specific context.
/// </para>
/// </summary>
internal sealed class AgentMemoryResolver(AgentEntity agent, string aiNodeId) : IAgentMemoryProxy
{
    private const string LlmMemoryPortName = "llm_memory";
    private const string KnowledgePortName = "knowledge";

    public async ValueTask<string> RetrieveAsync(string query, int topK, CancellationToken ct)
    {
        try
        {
            return await RetrieveCoreAsync(query, topK, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<string> RetrieveCoreAsync(string query, int topK, CancellationToken ct)
    {
        var graph = agent.Graph;

        // ── Resolve embedding provider ─────────────────────────────────────
        var memoryConnection = graph.Connections.FirstOrDefault(c => string.Equals(c.To, aiNodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, LlmMemoryPortName, StringComparison.OrdinalIgnoreCase));

        if (memoryConnection is null)
            return string.Empty;

        var memoryNode = graph.FindNode(memoryConnection.From);
        if (memoryNode is null)
            return string.Empty;

        var embeddingProvider = NodeInstanceRegistry.GetAs<IEmbeddingProvider>(memoryNode.NodeType);
        if (embeddingProvider is null)
            return string.Empty;

        var memoryResourceType = ResourceNodeRegistry.ResolveResourceType(memoryNode.NodeType) ?? ResourcePortCategory.LlmMemory;
        // Passing the graph lets the memory node resolve its OWN "embeddings" resource port, if
        // one is wired to it (see EmbeddingProviderResolver / ResourceBinding.Definition).
        var memoryBinding = new ResourceBinding(memoryConnection.From, memoryConnection.FromPort, memoryNode, memoryResourceType, state: null, definition: graph);

        // ── Resolve knowledge sources ──────────────────────────────────────
        var knowledgeConnections = graph.Connections.Where(c => string.Equals(c.To, aiNodeId, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, KnowledgePortName, StringComparison.OrdinalIgnoreCase)).ToList();

        if (knowledgeConnections.Count == 0)
            return string.Empty;

        var documents = new List<KnowledgeDocument>();
        foreach (var conn in knowledgeConnections)
        {
            ct.ThrowIfCancellationRequested();

            var knowledgeNode = graph.FindNode(conn.From);
            if (knowledgeNode is null)
                continue;

            var knowledgeSource = NodeInstanceRegistry.GetAs<IKnowledgeSource>(knowledgeNode.NodeType);
            if (knowledgeSource is null)
                continue;

            var resourceType = ResourceNodeRegistry.ResolveResourceType(knowledgeNode.NodeType) ?? ResourcePortCategory.KnowledgeSource;
            var knowledgeBinding = new ResourceBinding(conn.From, conn.FromPort, knowledgeNode, resourceType);

            // Construct a minimal NodeConfig for the knowledge source node.
            var nodeConfig = new NodeConfig(graph, knowledgeNode, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
            var loaded = await knowledgeSource.LoadAsync(nodeConfig, knowledgeBinding, ct).ConfigureAwait(false);

            foreach (var doc in loaded)
            {
                if (string.IsNullOrWhiteSpace(doc.Text))
                    continue;

                var metadata = doc.Metadata is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(doc.Metadata, StringComparer.OrdinalIgnoreCase);

                metadata.TryAdd("knowledge_source_node_id", knowledgeNode.Id);
                metadata.TryAdd("knowledge_source_node_type", knowledgeNode.NodeType);
                documents.Add(doc with { Metadata = metadata });
            }
        }

        if (documents.Count == 0)
            return string.Empty;

        // ── Embed query + rank (or delegate to a real vector store, if connected) ──────────
        var queryEmbedding = await embeddingProvider.CreateEmbeddingAsync(memoryBinding, query, ct).ConfigureAwait(false);

        if (embeddingProvider is IVectorStoreProvider vectorStore) // the same node object serves both roles
        {
            return await AgentMemoryVectorStoreRetrieval.RetrieveAsync(vectorStore, embeddingProvider, memoryBinding, documents, queryEmbedding, topK, ct).ConfigureAwait(false);
        }

        var docEmbeddings = await embeddingProvider.CreateBatchEmbeddingsAsync(memoryBinding, documents.Select(d => d.Text), ct).ConfigureAwait(false);

        var topMatches = KnowledgeRetrieval.RankTopMatches(queryEmbedding, documents, docEmbeddings, topK);
        return KnowledgeRetrieval.FormatContext(topMatches, "Knowledge");
    }

    // ── Static factory ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="AgentMemoryResolver"/> if the agent has an <c>llm_memory</c>
    /// resource connected to its <c>ai.node</c>, otherwise null.
    /// </summary>
    public static AgentMemoryResolver? TryCreate(AgentEntity agent)
    {
        const string AiNodeType = "ai.node";

        var aiNode = agent.Graph.Nodes.FirstOrDefault(n => string.Equals(n.NodeType, AiNodeType, StringComparison.OrdinalIgnoreCase));

        if (aiNode is null)
            return null;

        var hasMemory = agent.Graph.Connections.Any(c => string.Equals(c.To, aiNode.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, "llm_memory", StringComparison.OrdinalIgnoreCase));

        return hasMemory ? new AgentMemoryResolver(agent, aiNode.Id) : null;
    }
}
