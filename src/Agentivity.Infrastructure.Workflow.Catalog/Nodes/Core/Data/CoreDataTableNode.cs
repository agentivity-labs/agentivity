using System.Text.Json;
using Agentivity.Domain.DataTables;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.DataTables.Runtime;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;

/// <summary>
/// Provides CRUD operations on a shared Data Table asset (bound via the <c>data_table</c> resource
/// port — see <see cref="WorkspaceDataTableNode"/>).
/// Operations: insert, update, delete, select, find. A select searches the table like the agent's Data Table tool does — conditions
/// (<c>where</c>), a sort (<c>order_by</c>), a page (<c>limit</c>, <c>offset</c>) — and gives the total found; without a limit it
/// gives every row found, as it always did.
/// </summary>
public sealed class CoreDataTableNode : ToolNode
{
    /// <summary>How a query names the table: whatever its name in Studio, which may change.</summary>
    public const string QueryTableName = "datatable";

    // How long one SQL query may run before it is stopped.
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    public CoreDataTableNode()
        : base("core.datatable")
    {
        DefineMetadata("DataTable", "Provides CRUD operations on a shared Data Table asset.", "core.data", whenToUse: "To add, change, delete or look up rows of a Data Table during a run, or to compute totals on it with one read-only SQL SELECT. Connect a Data Table step to say which table.");
        DefineEffects(NodeEffects.WritesInternal);

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");
        DefineResourceInputPort("data_table", "Data Table", ResourcePortCategory.DataTable, "The shared Data Table asset to operate on", isRequired: true);

        DefineChoice("operation", [("insert", "Insert"), ("update", "Update"), ("delete", "Delete"), ("select", "Select"), ("find", "Find"), ("query", "Query (SQL)")], "Operation to perform", "select", ui: new ArgumentUiMetadata { Group = "Table", Order = 20 }, required: true);
        DefineInput(
            "sql",
            ArgumentType.Text,
            $"One read-only SQL SELECT on the table, which it names {QueryTableName} (query): totals, groups, averages… e.g. SELECT customer, SUM(amount) AS total FROM {QueryTableName} GROUP BY customer. Done by the database, on this table only; at most 'limit' rows, with the total.",
            ui: new ArgumentUiMetadata
            {
                Group = "Query",
                Order = 25,
                Multiline = true,
                Rows = 6,
            },
            required: false
        );
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
            },
            required: false
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
            },
            required: true
        );
        DefineInput(
            "where",
            ArgumentType.Text,
            "Conditions a row must all meet (select), as JSON: [{ \"column\": \"amount\", \"op\": \">\", \"value\": 100 }]. Operators: " + string.Join(", ", DataTableOperators.Names) + "; 'between' takes [lowest, highest].",
            ui: new ArgumentUiMetadata
            {
                Group = "Selection",
                Order = 45,
                Multiline = true,
                Rows = 4,
                Format = "json",
            },
            required: false
        );
        DefineInput("where_column", ArgumentType.Text, "Column name for where clause (select)", ui: new ArgumentUiMetadata { Group = "Selection", Order = 50 }, required: false);
        DefineInput("where_value", ArgumentType.Text, "Value for where clause (select)", ui: new ArgumentUiMetadata { Group = "Selection", Order = 60 }, required: false);
        DefineInput("order_by", ArgumentType.Text, "How to sort the rows found (select), e.g. \"amount desc, customer\" — by id when empty.", ui: new ArgumentUiMetadata { Group = "Selection", Order = 70 }, required: false);
        DefineInput("limit", ArgumentType.Number, "The most rows to give (select, query) — 1000 at most, and when empty; 0 gives only the total (select).", ui: new ArgumentUiMetadata { Group = "Selection", Order = 80, Min = 0, Step = 1 }, required: false);
        DefineInput("offset", ArgumentType.Number, "How many rows found to skip (select), to read the next page.", ui: new ArgumentUiMetadata { Group = "Selection", Order = 90, Min = 0, Step = 1 }, required: false);

        DefineOutput("row", ArgumentType.Text, "Affected row (insert/update/find)");
        DefineOutput("rows", ArgumentType.Text, "Selected rows (select)");
        DefineOutput("success", ArgumentType.Boolean, "Operation succeeded");
        DefineOutput("count", ArgumentType.Number, "Number of affected/selected rows");
        DefineOutput("total", ArgumentType.Number, "Number of rows the select found in all, even beyond its limit");

        DefineIcon(NodeIcon.Embedded("datatable"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);
        string operation = args.RequireText("operation").ToLowerInvariant();

        (IDataTableResource Tool, ResourceBinding Binding) resolved = ResourceResolver.RequireToolNode<IDataTableResource>(config, "data_table", ResourcePortCategory.DataTable);
        // The table itself, in the workspace's database: what is written there is written at once.
        LiveDataTable table = await resolved.Tool.ResolveAsync(resolved.Binding, cancellationToken).ConfigureAwait(false);

        IDictionary<string, object?> result = operation switch
        {
            "insert" => await ExecuteInsertAsync(table, args, cancellationToken).ConfigureAwait(false),
            "update" => await ExecuteUpdateAsync(table, args, cancellationToken).ConfigureAwait(false),
            "delete" => await ExecuteDeleteAsync(table, args, cancellationToken).ConfigureAwait(false),
            "select" => await ExecuteSelectAsync(table, args, cancellationToken).ConfigureAwait(false),
            "find" => await ExecuteFindAsync(table, args, cancellationToken).ConfigureAwait(false),
            "query" => await ExecuteQueryAsync(table, args, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown operation: {operation}. Valid operations: insert, update, delete, select, find, query"),
        };

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

    private static async Task<IDictionary<string, object?>> ExecuteInsertAsync(LiveDataTable table, ArgumentInputValues args, CancellationToken cancellationToken)
    {
        object? data = args.GetRaw("data");
        Dictionary<string, object?> dataDict = DataTableRowDataMapping.ConvertToDictionary(data);

        DataTableRow row = await table.InsertAsync(dataDict, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = row.ToDictionary(),
            ["rows"] = null,
            ["success"] = true,
            ["count"] = 1,
        };
    }

    private static async Task<IDictionary<string, object?>> ExecuteUpdateAsync(LiveDataTable table, ArgumentInputValues args, CancellationToken cancellationToken)
    {
        long id = args.RequireLong("id");
        object? data = args.GetRaw("data");
        Dictionary<string, object?> dataDict = DataTableRowDataMapping.ConvertToDictionary(data);

        DataTableRow? row = await table.UpdateAsync(id, dataDict, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = row?.ToDictionary(),
            ["rows"] = null,
            ["success"] = row is not null,
            ["count"] = row is not null ? 1 : 0,
        };
    }

    private static async Task<IDictionary<string, object?>> ExecuteDeleteAsync(LiveDataTable table, ArgumentInputValues args, CancellationToken cancellationToken)
    {
        long id = args.RequireLong("id");
        bool success = await table.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = null,
            ["rows"] = null,
            ["success"] = success,
            ["count"] = success ? 1 : 0,
        };
    }

    private static async Task<IDictionary<string, object?>> ExecuteSelectAsync(LiveDataTable table, ArgumentInputValues args, CancellationToken cancellationToken)
    {
        var where = new List<DataTableFilter>(
            args.GetRaw("where") switch
            {
                null => [],
                JsonElement json => DataTableQueryJson.ParseWhere(json),
                string text => DataTableQueryJson.ParseWhere(text),
                var other => DataTableQueryJson.ParseWhere(JsonSerializer.SerializeToElement(other)),
            }
        );
        // The single-column filter of before: one more condition.
        string? whereColumn = args.GetText("where_column");
        if (!string.IsNullOrWhiteSpace(whereColumn))
        {
            where.Add(new DataTableCondition(whereColumn, DataTableOperator.Equals, args.GetRaw("where_value")));
        }

        // Done by the database: one page at most, and the total found.
        DataTableSearchResult found = await table.SearchAsync(new DataTableQuery(where, DataTableQueryJson.ParseOrderBy(args.GetText("order_by")), args.GetInt("limit"), args.GetInt("offset") ?? 0), cancellationToken).ConfigureAwait(false);
        var rowDicts = found.Rows.Select(r => r.ToDictionary()).ToList();

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = rowDicts.Count > 0 ? rowDicts[0] : null,
            ["rows"] = rowDicts,
            ["success"] = true,
            ["count"] = rowDicts.Count,
            ["total"] = found.Total,
        };
    }

    // One SELECT written in the workflow (or made by an earlier step): done by the database, on the table itself, read-only and on
    // nothing else — the same guard as an agent's query (IDataTableStore.QueryAsync). What it cannot do fails the step, saying why.
    private static async Task<IDictionary<string, object?>> ExecuteQueryAsync(LiveDataTable table, ArgumentInputValues args, CancellationToken cancellationToken)
    {
        string sql = args.GetText("sql") is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidOperationException($"A query needs its 'sql': one SELECT on the table, named {QueryTableName}.");
        int most = Math.Min(args.GetInt("limit") ?? Agentivity.Infrastructure.Storage.Workspace.IDataTableStore.MaxPageSize, Agentivity.Infrastructure.Storage.Workspace.IDataTableStore.MaxPageSize);

        var result = await table.QueryAsync(QueryTableName, sql, most, QueryTimeout, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = result.Rows.Count > 0 ? result.Rows[0] : null,
            ["rows"] = result.Rows,
            ["success"] = true,
            ["count"] = result.Rows.Count,
            ["total"] = result.Total ?? result.Rows.Count,
        };
    }

    private static async Task<IDictionary<string, object?>> ExecuteFindAsync(LiveDataTable table, ArgumentInputValues args, CancellationToken cancellationToken)
    {
        long id = args.RequireLong("id");
        DataTableRow? row = await table.FindAsync(id, cancellationToken).ConfigureAwait(false);

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["row"] = row?.ToDictionary(),
            ["rows"] = null,
            ["success"] = row is not null,
            ["count"] = row is not null ? 1 : 0,
        };
    }

}
