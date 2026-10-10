using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;

/// <summary>
/// Implemented by the resource node connected to a <see cref="CoreDataTableNode"/>'s
/// <c>data_table</c> port — resolves the live, shared <see cref="DataTable"/> for that binding and
/// persists changes back to it. Mirrors the <c>IAgentSession</c> resource-node pattern.
/// </summary>
public interface IDataTableResource
{
    ValueTask<DataTable> ResolveAsync(ResourceBinding binding, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(ResourceBinding binding, CancellationToken cancellationToken = default);
}
