using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.DataTables.Runtime;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Data;

/// <summary>
/// Exposes CRUD on a shared Data Table asset as LLM-callable AI Agent tools — a single
/// self-contained node: pick the table by id (<c>table_id</c>) directly on this node, no separate
/// <c>workspace.datatable</c> resource node or connection required. Wire this node's
/// <c>resource</c> output into an <c>ai.node</c>'s <c>tools</c> port and it's ready.
/// <para>
/// Multiple independent agents (e.g. a Trip Manager and a separate Réservation agent) read and
/// write the same shared table without ever talking to each other directly — each simply gets its
/// own instance of this node with the same <c>table_id</c>; the sharing comes from both instances
/// addressing the same table asset, not from any wiring between them.
/// </para>
/// <para>
/// <c>allow_read</c>/<c>allow_insert</c>/<c>allow_update</c>/<c>allow_delete</c> gate which of
/// this instance's operations are exposed to the LLM at all (read covers both <c>_select</c> and
/// <c>_find</c>) — default true each, so existing behavior is unchanged unless explicitly
/// narrowed. Enforced twice: <see cref="ListToolsAsync"/> never advertises a disabled operation,
/// and <see cref="CallToolAsync"/> refuses it too, in case a caller still names it directly.
/// </para>
/// </summary>
public sealed class AIAgentDataTableToolNode : ResourceNode, IAgentTool
{
    private const string DefaultPrefix = "datatable";

    // How many rows one select gives at most, unless the tool says otherwise: enough to work with, never a whole big table.
    private const int DefaultMaxRows = 100;

    // How long one SQL query may run before it is stopped.
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly DataTableRowStore _rowStore;

    public AIAgentDataTableToolNode(DataTableRowStore rowStore)
        : base("ai.tools.data.datatable")
    {
        _rowStore = rowStore ?? throw new ArgumentNullException(nameof(rowStore));
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("AI Agent Data Table Tool", "Exposes CRUD on a shared Data Table asset as LLM-callable tools.", "ai.tools.data", whenToUse: "To let an agent search, add, change or delete rows of a Data Table, for example bookings or leads shared by several agents. Turn off what the agent must not do; questions in SQL (totals, averages) stay off unless you turn them on.");
        DefineEffects(NodeEffects.WritesInternal);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Data Table Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);
        DefineAssetInput("table_id", "datatable", "The shared Data Table asset this tool reads and writes.", group: "Table", order: 0, required: true);
        DefineInput(
            "tool_name_prefix",
            ArgumentType.Text,
            "Prefix for this instance's tool names, e.g. \"trips\" → trips_select, trips_insert, trips_update, trips_delete, trips_find. Required whenever an agent has more than one Data Table tool connected — tool names must be unique per agent.",
            DefaultPrefix,
            ui: new ArgumentUiMetadata { Group = "Tools", Order = 10 },
            required: false
        );
        DefineInput(
            "max_rows",
            ArgumentType.Number,
            $"The most rows one select gives the agent (default {DefaultMaxRows}; 0 for no limit). When a search finds more, the agent is told how many there are and how to get the next ones.",
            DefaultMaxRows,
            ui: new ArgumentUiMetadata { Group = "Tools", Order = 11, Min = 0, Step = 1 },
            required: false
        );
        DefineInput("allow_read", ArgumentType.Boolean, "Expose reading rows (select/find) to the agent.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 20 }, required: false);
        DefineInput("allow_insert", ArgumentType.Boolean, "Expose inserting new rows to the agent.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 21 }, required: false);
        DefineInput("allow_update", ArgumentType.Boolean, "Expose updating existing rows to the agent.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 22 }, required: false);
        DefineInput("allow_delete", ArgumentType.Boolean, "Expose deleting rows to the agent.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 23 }, required: false);
        DefineInput(
            "allow_query",
            ArgumentType.Boolean,
            "Expose read-only SQL on this table to the agent (one SELECT at a time: totals, groups, averages…). Off unless turned on.",
            false,
            ui: new ArgumentUiMetadata { Group = "Permissions", Order = 24 },
            required: false
        );

        DefineIcon(NodeIcon.Embedded("datatable"));
    }

    private static string ResolvePrefix(ResourceBinding binding)
    {
        string? prefix = binding.GetString("tool_name_prefix")?.Trim();
        return string.IsNullOrEmpty(prefix) ? DefaultPrefix : prefix;
    }

    private static bool ResolveFlag(ResourceBinding binding, string key) => binding.GetBool(key) ?? true;

    /// <summary>
    /// The tools advertised to the LLM. Insert/update/select describe the table's REAL columns
    /// (names, types, which are required) straight from its schema — otherwise the model only
    /// learns them from the agent's prompt and, when the prompt doesn't spell them out, invents
    /// column names and the insert fails (confirmed in a live run). Unlike the other operations,
    /// this therefore loads the table: a missing or unknown <c>table_id</c> fails here with the
    /// same clear message a call would give, instead of surfacing later mid-conversation.
    /// </summary>
    public async ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string prefix = ResolvePrefix(binding);
        LiveDataTable? table = null;
        string? tableId = binding.GetString("table_id")?.Trim();
        if (!string.IsNullOrEmpty(tableId))
        {
            table = await _rowStore.OpenAsync(tableId, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<DataTableColumn> columns = table?.Schema.Columns ?? [];
        var tools = new List<JsonObject>();

        if (ResolveFlag(binding, "allow_read"))
        {
            int? maxRows = ResolveMaxRows(binding);
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_select",
                "Search the rows of the Data Table: conditions in 'where' (all must be true), a sort in 'order_by', the columns to return in 'columns'. "
                    + (maxRows is { } max ? $"At most {max} rows come back at once; the answer always gives the total found, and 'next_offset' when there are more. " : "The answer always gives the total found. ")
                    + "Ask for only what you need: narrow with conditions rather than reading everything, and use limit 0 to count without reading.",
                BuildSelectSchema(table?.SearchableColumns ?? []).ToJsonString(),
                allowToolStateReuse: true
            ));
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_find",
                "Fetch a single row by its numeric id.",
                """
                {
                  "type": "object",
                  "properties": { "id": { "type": "number", "description": "The row's id." } },
                  "required": ["id"],
                  "additionalProperties": false
                }
                """,
                allowToolStateReuse: true
            ));
        }

        if (table is not null && binding.GetBool("allow_query") == true)
        {
            int? maxRows = ResolveMaxRows(binding);
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_query",
                $"Run ONE read-only SQL SELECT ({table.SqlDialect}) on the table {table.DescribeForQuery(prefix)} "
                    + "It can only read this table: no change, no other table. "
                    + (maxRows is { } max ? $"At most {max} rows come back; when there are more, the answer gives the total. " : string.Empty)
                    + "Let SQL do the work — COUNT, SUM, AVG, GROUP BY, WHERE — rather than reading rows to count them.",
                """
                {
                  "type": "object",
                  "properties": { "sql": { "type": "string", "description": "One SELECT statement." } },
                  "required": ["sql"],
                  "additionalProperties": false
                }
                """,
                allowToolStateReuse: true
            ));
        }

        if (ResolveFlag(binding, "allow_insert"))
        {
            var insertSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["data"] = BuildRowSchema(columns, "Column values for the new row.", requireMandatoryColumns: true) },
                ["required"] = new JsonArray("data"),
                ["additionalProperties"] = false,
            };
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_insert",
                "Insert a new row into the Data Table. 'data' is a JSON object using exactly this table's columns.",
                insertSchema.ToJsonString()
            ));
        }

        if (ResolveFlag(binding, "allow_update"))
        {
            var updateSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "number", ["description"] = "The row's id." },
                    ["data"] = BuildRowSchema(columns, "Column values to merge into the row — only the columns being changed.", requireMandatoryColumns: false),
                },
                ["required"] = new JsonArray("id", "data"),
                ["additionalProperties"] = false,
            };
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_update",
                "Update an existing row of the Data Table by id. 'data' only needs to contain the columns being changed.",
                updateSchema.ToJsonString()
            ));
        }

        if (ResolveFlag(binding, "allow_delete"))
        {
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_delete",
                "Delete a row by id.",
                """
                {
                  "type": "object",
                  "properties": { "id": { "type": "number", "description": "The row's id." } },
                  "required": ["id"],
                  "additionalProperties": false
                }
                """
            ));
        }

        return tools;
    }

    private static int? ResolveMaxRows(ResourceBinding binding)
    {
        double? configured = binding.GetDouble("max_rows");
        return configured is null ? DefaultMaxRows : configured.Value <= 0 ? null : (int)configured.Value;
    }

    /// <summary>The arguments of a search, on the table's real columns (any column when the table is not known yet).</summary>
    private static JsonObject BuildSelectSchema(IReadOnlyList<string> columnNames)
    {
        JsonObject ColumnName(string description)
        {
            var column = new JsonObject { ["type"] = "string", ["description"] = description };
            if (columnNames.Count > 0)
            {
                column["enum"] = new JsonArray(columnNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            }

            return column;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["where"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Conditions a row must all meet. Texts are compared without regard to case; for a list column, 'contains' looks for an item.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["column"] = ColumnName("The column to test."),
                            ["op"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(DataTableOperators.Names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) },
                            ["value"] = new JsonObject { ["description"] = "The value to compare with — for 'between', [lowest, highest]; none for 'is_empty' and 'is_not_empty'." },
                        },
                        ["required"] = new JsonArray("column", "op"),
                        ["additionalProperties"] = false,
                    },
                },
                ["order_by"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "How to sort the rows (by id when omitted).",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["column"] = ColumnName("The column to sort on."), ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("asc", "desc") } },
                        ["required"] = new JsonArray("column"),
                        ["additionalProperties"] = false,
                    },
                },
                ["columns"] = new JsonObject { ["type"] = "array", ["description"] = "The columns to return (every column when omitted); the id always comes.", ["items"] = ColumnName("A column to return.") },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "How many rows to return at most; 0 returns only the total." },
                ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "How many rows found to skip — the 'next_offset' of the previous answer, to read the next ones." },
            },
            ["additionalProperties"] = false,
        };
    }

    /// <summary>
    /// The JSON Schema of one row's column values, built from the table's own columns. With no
    /// columns known (no table configured yet) it degrades to a plain "any object".
    /// </summary>
    private static JsonObject BuildRowSchema(IReadOnlyList<DataTableColumn> columns, string description, bool requireMandatoryColumns)
    {
        if (columns.Count == 0)
        {
            return new JsonObject { ["type"] = "object", ["description"] = description };
        }

        var properties = new JsonObject();
        foreach (DataTableColumn column in columns)
        {
            properties[column.Name] = BuildColumnSchema(column);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["description"] = description,
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };

        if (requireMandatoryColumns)
        {
            // Same rule the table itself applies (DataTableColumn.IsValidValue): a required column
            // is only truly mandatory when it has no default to fall back on.
            string[] mandatory = columns.Where(c => c.Required && c.Default is null).Select(c => c.Name).ToArray();
            if (mandatory.Length > 0)
            {
                schema["required"] = new JsonArray(mandatory.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            }
        }

        return schema;
    }

    private static JsonObject BuildColumnSchema(DataTableColumn column)
    {
        var schema = column.Type switch
        {
            ArgumentType.Number => new JsonObject { ["type"] = "number" },
            ArgumentType.Boolean => new JsonObject { ["type"] = "boolean" },
            ArgumentType.TextList => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ArgumentType.Structured => new JsonObject { ["type"] = "object" },
            _ => new JsonObject { ["type"] = "string" },
        };

        var notes = new List<string>();
        if (column.Required && column.Default is null)
        {
            notes.Add("Required.");
        }

        if (column.Default is not null)
        {
            notes.Add($"Defaults to {JsonSerializer.Serialize(column.Default)} when omitted.");
        }

        if (notes.Count > 0)
        {
            schema["description"] = string.Join(' ', notes);
        }

        return schema;
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        string? tableId = binding.GetString("table_id")?.Trim();
        if (string.IsNullOrEmpty(tableId))
        {
            // No amount of replanning fixes a missing table_id on this node — that's a Studio
            // configuration error, not something the calling agent can recover from.
            throw new InvalidOperationException($"'{Name}' has no 'table_id' configured.");
        }

        // The table itself, in the workspace's database: what is written there is written at once.
        LiveDataTable table = await _rowStore.OpenAsync(tableId, cancellationToken).ConfigureAwait(false);

        string prefix = ResolvePrefix(binding);
        string operation = toolName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) ? toolName[(prefix.Length + 1)..] : toolName;

        bool Allowed(string flag) => ResolveFlag(binding, flag);
        if (operation.ToLowerInvariant() is "select" or "find" && !Allowed("allow_read"))
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");
        if (operation.Equals("query", StringComparison.OrdinalIgnoreCase) && binding.GetBool("allow_query") != true)
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");
        if (operation.Equals("insert", StringComparison.OrdinalIgnoreCase) && !Allowed("allow_insert"))
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");
        if (operation.Equals("update", StringComparison.OrdinalIgnoreCase) && !Allowed("allow_update"))
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");
        if (operation.Equals("delete", StringComparison.OrdinalIgnoreCase) && !Allowed("allow_delete"))
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");

        return operation.ToLowerInvariant() switch
        {
            "select" => await ExecuteSelectAsync(table, arguments, prefix, ResolveMaxRows(binding), cancellationToken).ConfigureAwait(false),
            "query" => await ExecuteQueryAsync(table, arguments, prefix, ResolveMaxRows(binding), cancellationToken).ConfigureAwait(false),
            "find" => await ExecuteFindAsync(table, arguments, cancellationToken).ConfigureAwait(false),
            "insert" => await ExecuteInsertAsync(table, arguments, cancellationToken).ConfigureAwait(false),
            "update" => await ExecuteUpdateAsync(table, arguments, cancellationToken).ConfigureAwait(false),
            "delete" => await ExecuteDeleteAsync(table, arguments, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Tool '{toolName}' is not exposed by '{Name}'."),
        };
    }

    /// <summary>
    /// A search, at most <paramref name="maxRows"/> rows at once. The answer always says how many rows were found, and — when that is
    /// more than came back — how to read the next ones, narrow the search or count: an agent never takes a page for the whole table.
    /// A search that cannot be done comes back as an error the agent can correct (the columns there are, the operators that fit).
    /// </summary>
    private static async Task<JsonObject> ExecuteSelectAsync(LiveDataTable table, JsonElement arguments, string prefix, int? maxRows, CancellationToken ct)
    {
        DataTableSearchResult found;
        IReadOnlyList<string>? only;
        try
        {
            var where = new List<DataTableFilter>(Property(arguments, "where") is { } w ? DataTableQueryJson.ParseWhere(w) : []);
            // The single-column filter the tool had before: still understood, as one condition.
            if (Property(arguments, "where_column") is { ValueKind: JsonValueKind.String } column && !string.IsNullOrWhiteSpace(column.GetString()))
            {
                where.Add(new DataTableCondition(column.GetString()!, DataTableOperator.Equals, Property(arguments, "where_value")));
            }

            int? limit = Property(arguments, "limit") is { ValueKind: JsonValueKind.Number } l ? (int)l.GetDouble() : null;
            int offset = Property(arguments, "offset") is { ValueKind: JsonValueKind.Number } o ? (int)o.GetDouble() : 0;
            if (maxRows is { } max)
            {
                limit = limit is null ? max : Math.Min(limit.Value, max);
            }

            only = Property(arguments, "columns") is { ValueKind: JsonValueKind.Array } c ? c.EnumerateArray().Select(n => table.ColumnNamed(n.GetString() ?? string.Empty)).ToList() : null;
            // Done by the database: one page at most, and the total found.
            found = await table.SearchAsync(new DataTableQuery(where, Property(arguments, "order_by") is { } sort ? DataTableQueryJson.ParseOrderBy(sort) : null, limit, offset), ct).ConfigureAwait(false);
        }
        catch (DataTableQueryException ex)
        {
            return McpToolResultJson.CreateFailedResult(ex.Message, "invalid_query");
        }

        var rows = found.Rows.Select(r => only is null ? r.ToDictionary() : r.ToDictionary().Where(p => p.Key == "id" || only.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(StringComparer.OrdinalIgnoreCase)).ToList();
        var answer = new Dictionary<string, object?> { ["rows"] = rows, ["count"] = rows.Count, ["total"] = found.Total, ["offset"] = found.Offset };
        if (found.NextOffset is { } next)
        {
            answer["next_offset"] = next;
            answer["note"] = $"Rows {found.Offset + 1} to {next} of {found.Total} found. To read the next ones, call {prefix}_select again with the same arguments and offset {next}. "
                + $"To find fewer, add conditions in 'where'; to count without reading, call it with limit 0.";
        }

        return SerializeResult(answer);
    }

    /// <summary>
    /// A read-only SQL query, done by the database on the table itself and nothing else: at most <paramref name="maxRows"/> rows, the
    /// total when there are more, and a query that cannot be run — refused, wrong, too long — comes back as an error the agent can fix.
    /// </summary>
    private static async Task<JsonObject> ExecuteQueryAsync(LiveDataTable table, JsonElement arguments, string prefix, int? maxRows, CancellationToken ct)
    {
        if (Property(arguments, "sql") is not { ValueKind: JsonValueKind.String } sql)
        {
            return McpToolResultJson.CreateFailedResult("The 'sql' argument is required: one SELECT statement.", "invalid_arguments");
        }

        DataTableQueryResult result;
        try
        {
            // Done by the database, on the table itself — read-only, that table only (see IDataTableStore.QueryAsync).
            result = await table.QueryAsync(prefix, sql.GetString()!, maxRows, QueryTimeout, ct).ConfigureAwait(false);
        }
        catch (DataTableQueryException ex)
        {
            return McpToolResultJson.CreateFailedResult(ex.Message, "invalid_query");
        }

        var answer = new Dictionary<string, object?> { ["columns"] = result.Columns, ["rows"] = result.Rows, ["count"] = result.Rows.Count };
        if (result.Total is { } total)
        {
            answer["total"] = total;
            answer["note"] = $"The query gave {total} rows; only the first {result.Rows.Count} are here. Read the rest a page at a time with LIMIT and OFFSET, or let the query do more of the work (WHERE, GROUP BY, COUNT, SUM).";
        }

        return SerializeResult(answer);
    }

    private static JsonElement? Property(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

    private static async ValueTask<JsonObject> ExecuteFindAsync(LiveDataTable table, JsonElement arguments, CancellationToken ct)
    {
        if (!TryGetRowId(arguments, out long id, out JsonObject? error))
        {
            return error!;
        }

        DataTableRow? row = await table.FindAsync(id, ct).ConfigureAwait(false);
        return row is null ? McpToolResultJson.CreateFailedResult($"No row with id {id} was found.", "row_not_found") : SerializeResult(row.ToDictionary());
    }

    private static async ValueTask<JsonObject> ExecuteInsertAsync(LiveDataTable table, JsonElement arguments, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            return McpToolResultJson.CreateFailedResult("The 'data' argument must be a JSON object of column values.", "invalid_arguments");
        }

        DataTableRow row = await table.InsertAsync(DataTableRowDataMapping.ConvertToDictionary(dataElement), ct).ConfigureAwait(false);
        return SerializeResult(row.ToDictionary());
    }

    private static async ValueTask<JsonObject> ExecuteUpdateAsync(LiveDataTable table, JsonElement arguments, CancellationToken ct)
    {
        if (!TryGetRowId(arguments, out long id, out JsonObject? error))
        {
            return error!;
        }

        if (!arguments.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            return McpToolResultJson.CreateFailedResult("The 'data' argument must be a JSON object of column values.", "invalid_arguments");
        }

        DataTableRow? row = await table.UpdateAsync(id, DataTableRowDataMapping.ConvertToDictionary(dataElement), ct).ConfigureAwait(false);
        if (row is null)
        {
            return McpToolResultJson.CreateFailedResult($"No row with id {id} was found.", "row_not_found");
        }

        return SerializeResult(row.ToDictionary());
    }

    private static async ValueTask<JsonObject> ExecuteDeleteAsync(LiveDataTable table, JsonElement arguments, CancellationToken ct)
    {
        if (!TryGetRowId(arguments, out long id, out JsonObject? error))
        {
            return error!;
        }

        bool success = await table.DeleteAsync(id, ct).ConfigureAwait(false);
        if (!success)
        {
            return McpToolResultJson.CreateFailedResult($"No row with id {id} was found.", "row_not_found");
        }

        return SerializeResult(new { success = true });
    }

    private static bool TryGetRowId(JsonElement arguments, out long id, out JsonObject? error)
    {
        if (arguments.TryGetProperty("id", out JsonElement idElement) && idElement.TryGetInt64(out id))
        {
            error = null;
            return true;
        }

        id = 0;
        error = McpToolResultJson.CreateFailedResult("The 'id' argument is required and must be a number.", "invalid_arguments");
        return false;
    }

    private static JsonObject SerializeResult(object payload) => McpToolResultJson.CreateTextResult(JsonSerializer.Serialize(payload, SerializerOptions), isError: false);
}
