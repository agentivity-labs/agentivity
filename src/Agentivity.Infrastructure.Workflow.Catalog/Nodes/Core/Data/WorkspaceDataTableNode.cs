using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.DataTables.Runtime;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;

/// <summary>
/// Resource node that references a shared Data Table asset by id. Connects to a
/// <see cref="CoreDataTableNode"/>'s <c>data_table</c> port — same pattern as an AI Agent Session
/// resource node connecting to <c>AiNode</c>'s <c>session</c> port.
/// </summary>
public sealed class WorkspaceDataTableNode : ResourceNode, IDataTableResource
{
    private readonly DataTableRowStore _rowStore;

    public WorkspaceDataTableNode(DataTableRowStore rowStore)
        : base("workspace.datatable")
    {
        _rowStore = rowStore ?? throw new ArgumentNullException(nameof(rowStore));
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.DataTable);

        DefineMetadata("Data Table", "References a shared Data Table asset (schema and rows managed under Resources).", "core.data");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Data Table", ResourcePortCategory.DataTable, maxConnections: -1);
        DefineAssetInput("table_id", "datatable", "The shared Data Table asset to bind to");

        DefineIcon(NodeIcon.Embedded("datatable"));
    }

    public ValueTask<DataTable> ResolveAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        var tableId = binding.RequireString("table_id");
        return _rowStore.GetOrLoadAsync(tableId, cancellationToken);
    }

    public ValueTask SaveAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        var tableId = binding.RequireString("table_id");
        return _rowStore.SaveAsync(tableId, cancellationToken);
    }
}
