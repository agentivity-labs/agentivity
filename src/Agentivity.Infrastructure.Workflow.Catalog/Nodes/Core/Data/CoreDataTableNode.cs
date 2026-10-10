using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;

/// <summary>
/// Provides CRUD operations on a shared Data Table asset (bound via the <c>data_table</c> resource
/// port — see <see cref="WorkspaceDataTableNode"/>).
/// Operations: insert, update, delete, select, find
/// </summary>
public sealed class CoreDataTableNode : ToolNode
{
    public CoreDataTableNode()
        : base("core.datatable")
    {
        DefineMetadata("DataTable", "Provides CRUD operations on a shared Data Table asset.", "core.data");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");
        DefineResourceInputPort("data_table", "Data Table", ResourcePortCategory.DataTable, "The shared Data Table asset to operate on", isRequired: true);

        DefineChoice("operation", [("insert", "Insert"), ("update", "Update"), ("delete", "Delete"), ("select", "Select"), ("find", "Find")], "Operation to perform", "select", ui: new ArgumentUiMetadata { Group = "Table", Order = 20 });
        DefineInput(
            "data",
            ArgumentType.Text,
            "Data for insert/update (object)",
            ui: new ArgumentUiMetadata
            {
                Group = "Data",
                Order = 30,
                Multiline = true,
                Rows = 10,
                Format = "json",
            }
        );
        DefineInput(
            "id",
            ArgumentType.Number,
            "Row ID for update/delete/find",
            ui: new ArgumentUiMetadata
            {
                Group = "Selection",
                Order = 40,
                Min = 1,
                Step = 1,
            }
        );
        DefineInput("where_column", ArgumentType.Text, "Column name for where clause (select)", ui: new ArgumentUiMetadata { Group = "Selection", Order = 50 });
        DefineInput("where_value", ArgumentType.Text, "Value for where clause (select)", ui: new ArgumentUiMetadata { Group = "Selection", Order = 60 });

        DefineOutput("row", ArgumentType.Text, "Affected row (insert/update/find)");
        DefineOutput("rows", ArgumentType.Text, "Selected rows (select)");
        DefineOutput("success", ArgumentType.Boolean, "Operation succeeded");
        DefineOutput("count", ArgumentType.Number, "Number of affected/selected rows");

        DefineIcon(NodeIcon.Embedded("datatable"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string operation = args.RequireText("operation").ToLowerInvariant();

        (IDataTableResource Tool, ResourceBinding Binding) resolved = ResourceResolver.RequireToolNode<IDataTableResource>(config, "data_table", ResourcePortCategory.DataTable);
        DataTable table = await resolved.Tool.ResolveAsync(resolved.Binding, cancellationToken).ConfigureAwait(false);

        IDictionary<string, object?> result = operation switch
        {
            "insert" => ExecuteInsert(table, args),
            "update" => ExecuteUpdate(table, args),
            "delete" => ExecuteDelete(table, args),
            "select" => ExecuteSelect(table, args),
            "find" => ExecuteFind(table, args),
            _ => throw new InvalidOperationException($"Unknown operation: {operation}. Valid operations: insert, update, delete, select, find"),
        };

        if (operation is "insert" or "update" or "delete")
        {
            await resolved.Tool.SaveAsync(resolved.Binding, cancellationToken).ConfigureAwait(false);
        }

        return ConvertToNodeResult(result);
    }

    private static NodeResult ConvertToNodeResult(IDictionary<string, object?> result)
    {
        var nodeResult = NodeResult.Create();
        foreach ((string? key, object? value) in result)
        {
            if (value is null)
                _ = nodeResult.AddText(key, null);
            else if (value is string s)
                _ = nodeResult.AddText(key, s);
            else
                _ =
                    value is int i ? nodeResult.AddNumber(key, i)
                    : value is long l ? nodeResult.AddNumber(key, l)
                    : value is bool b ? nodeResult.AddBoolean(key, b)
                    : nodeResult.AddText(key, System.Text.Json.JsonSerializer.Serialize(value));
        }
        return nodeResult;
    }

    private static IDictionary<string, object?> ExecuteInsert(DataTable table, ArgumentInputValues args)
    {
        object? data = args.GetRaw("data");
        Dictionary<string, object?> dataDict = DataTableRowDataMapping.ConvertToDictionary(data);

        DataTableRow row = table.Insert(dataDict);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = row.ToDictionary(),
            ["rows"] = null,
            ["success"] = true,
            ["count"] = 1,
        };
    }

    private static IDictionary<string, object?> ExecuteUpdate(DataTable table, ArgumentInputValues args)
    {
        long id = args.RequireLong("id");
        object? data = args.GetRaw("data");
        Dictionary<string, object?> dataDict = DataTableRowDataMapping.ConvertToDictionary(data);

        DataTableRow? row = table.Update(id, dataDict);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = row?.ToDictionary(),
            ["rows"] = null,
            ["success"] = row is not null,
            ["count"] = row is not null ? 1 : 0,
        };
    }

    private static IDictionary<string, object?> ExecuteDelete(DataTable table, ArgumentInputValues args)
    {
        long id = args.RequireLong("id");
        bool success = table.Delete(id);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = null,
            ["rows"] = null,
            ["success"] = success,
            ["count"] = success ? 1 : 0,
        };
    }

    private static IDictionary<string, object?> ExecuteSelect(DataTable table, ArgumentInputValues args)
    {
        string? whereColumn = args.GetText("where_column");
        object? whereValue = args.GetRaw("where_value");

        IReadOnlyList<DataTableRow> rows = !string.IsNullOrWhiteSpace(whereColumn) ? table.SelectWhere(whereColumn, whereValue) : table.SelectAll();
        var rowDicts = rows.Select(r => r.ToDictionary()).ToList();

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = rowDicts.Count > 0 ? rowDicts[0] : null,
            ["rows"] = rowDicts,
            ["success"] = true,
            ["count"] = rowDicts.Count,
        };
    }

    private static IDictionary<string, object?> ExecuteFind(DataTable table, ArgumentInputValues args)
    {
        long id = args.RequireLong("id");
        DataTableRow? row = table.FindById(id);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = row?.ToDictionary(),
            ["rows"] = null,
            ["success"] = row is not null,
            ["count"] = row is not null ? 1 : 0,
        };
    }

}
