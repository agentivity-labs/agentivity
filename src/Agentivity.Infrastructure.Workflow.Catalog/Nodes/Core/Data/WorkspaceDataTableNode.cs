using Agentivity.Domain.DataTables;
using Agentivity.Domain.Workflow;
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

        DefineMetadata("Data Table", "References a shared Data Table asset (schema and rows managed under Resources).", "core.data", whenToUse: "To choose which Data Table, created under Resources, a DataTable step works on. It does nothing by itself: connect it to a DataTable step.");
        DefineEffects(NodeEffects.WritesInternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "Data Table", ResourcePortCategory.DataTable, maxConnections: -1);
        DefineAssetInput("table_id", "datatable", "The shared Data Table asset to bind to", required: true);

        DefineIcon(NodeIcon.Embedded("datatable"));
    }

    public ValueTask<LiveDataTable> ResolveAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        var tableId = binding.RequireString("table_id");
        return _rowStore.OpenAsync(tableId, cancellationToken);
    }
}
