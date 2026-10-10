using Agentivity.Infrastructure.Core.Llm.Knowledge;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Interface for knowledge-source resource nodes.
/// The node is the source adapter - consumers resolve the node and ask it for normalized documents.
/// </summary>
public interface IKnowledgeSource
{
    ValueTask<IReadOnlyList<KnowledgeDocument>> LoadAsync(NodeConfig context, ResourceBinding binding, CancellationToken cancellationToken = default);
}
