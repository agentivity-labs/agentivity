using Agentivity.Infrastructure.Core.Llm.Knowledge;

namespace Agentivity.Infrastructure.Workflows.Resources;

public static class KnowledgeSourceResolver
{
    public static async ValueTask<IReadOnlyList<KnowledgeDocument>> ResolveDocumentsAsync(NodeConfig context, string portName, string resourceType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);

        var sources = ResourceResolver.ResolveToolNodes<IKnowledgeSource>(context, portName, resourceType);
        if (sources.Count == 0)
        {
            return [];
        }

        var documents = new List<KnowledgeDocument>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceConfig = new NodeConfig(context.Definition, source.Binding.SourceNode, context.State);
            var loaded = await source.Tool.LoadAsync(sourceConfig, source.Binding, cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
            {
                continue;
            }

            foreach (var document in loaded)
            {
                if (string.IsNullOrWhiteSpace(document.Text))
                {
                    continue;
                }

                var metadata = document.Metadata is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(document.Metadata, StringComparer.OrdinalIgnoreCase);

                metadata.TryAdd("knowledge_source_node_id", source.Binding.SourceNodeId);
                metadata.TryAdd("knowledge_source_node_type", source.Binding.SourceNode.NodeType);

                documents.Add(document with { Metadata = metadata });
            }
        }

        return documents;
    }
}
