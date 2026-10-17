using Agentivity.Infrastructure.DataTables.Runtime;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;

/// <summary>
/// Implemented by the resource node connected to a <see cref="CoreDataTableNode"/>'s
/// <c>data_table</c> port — resolves the shared table for that binding (<see cref="LiveDataTable"/>: what is written there is
/// written at once). Mirrors the <c>IAgentSession</c> resource-node pattern.
/// </summary>
public interface IDataTableResource
{
    ValueTask<LiveDataTable> ResolveAsync(ResourceBinding binding, CancellationToken cancellationToken = default);
}
