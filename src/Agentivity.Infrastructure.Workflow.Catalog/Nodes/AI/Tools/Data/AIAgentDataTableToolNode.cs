using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.DataTables.Runtime;
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

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly DataTableRowStore _rowStore;

    public AIAgentDataTableToolNode(DataTableRowStore rowStore)
        : base("ai.tools.data.datatable")
    {
        _rowStore = rowStore ?? throw new ArgumentNullException(nameof(rowStore));
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("AI Agent Data Table Tool", "Exposes CRUD on a shared Data Table asset as LLM-callable tools.", "ai.tools.data");
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
        DefineInput("allow_read", ArgumentType.Boolean, "Expose reading rows (select/find) to the agent.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 20 }, required: false);
        DefineInput("allow_insert", ArgumentType.Boolean, "Expose inserting new rows to the agent.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 21 }, required: false);
        DefineInput("allow_update", ArgumentType.Boolean, "Expose updating existing rows to the agent.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 22 }, required: false);
        DefineInput("allow_delete", ArgumentType.Boolean, "Expose deleting rows to the agent.", true, ui: new ArgumentUiMetadata { Group = "Permissions", Order = 23 }, required: false);

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
        DataTableSchema? schema = null;
        string? tableId = binding.GetString("table_id")?.Trim();
        if (!string.IsNullOrEmpty(tableId))
        {
            schema = (await _rowStore.GetOrLoadAsync(tableId, cancellationToken).ConfigureAwait(false)).Schema;
        }

        IReadOnlyList<DataTableColumn> columns = schema?.Columns ?? [];
        var tools = new List<JsonObject>();

        if (ResolveFlag(binding, "allow_read"))
        {
            var whereColumn = new JsonObject { ["type"] = "string", ["description"] = "Column name to filter on (optional)." };
            if (columns.Count > 0)
            {
                whereColumn["enum"] = new JsonArray(columns.Select(c => (JsonNode?)JsonValue.Create(c.Name)).ToArray());
            }

            var selectSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["where_column"] = whereColumn,
                    ["where_value"] = new JsonObject { ["type"] = "string", ["description"] = "Value that column must equal (optional, requires where_column)." },
                },
                ["additionalProperties"] = false,
            };
            tools.Add(LlmToolDefinitionJson.Create(
                $"{prefix}_select",
                "List rows from the Data Table, optionally filtered to rows where one column equals a value. Omit both arguments to get every row.",
                selectSchema.ToJsonString(),
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

        DataTable table = await _rowStore.GetOrLoadAsync(tableId, cancellationToken).ConfigureAwait(false);

        string prefix = ResolvePrefix(binding);
        string operation = toolName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) ? toolName[(prefix.Length + 1)..] : toolName;

        bool Allowed(string flag) => ResolveFlag(binding, flag);
        if (operation.ToLowerInvariant() is "select" or "find" && !Allowed("allow_read"))
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");
        if (operation.Equals("insert", StringComparison.OrdinalIgnoreCase) && !Allowed("allow_insert"))
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");
        if (operation.Equals("update", StringComparison.OrdinalIgnoreCase) && !Allowed("allow_update"))
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");
        if (operation.Equals("delete", StringComparison.OrdinalIgnoreCase) && !Allowed("allow_delete"))
            return McpToolResultJson.CreateFailedResult($"'{toolName}' is disabled on this Data Table tool.", "operation_not_allowed");

        return operation.ToLowerInvariant() switch
        {
            "select" => ExecuteSelect(table, arguments),
            "find" => await ExecuteFindAsync(table, arguments).ConfigureAwait(false),
            "insert" => await ExecuteInsertAsync(table, arguments, tableId, cancellationToken).ConfigureAwait(false),
            "update" => await ExecuteUpdateAsync(table, arguments, tableId, cancellationToken).ConfigureAwait(false),
            "delete" => await ExecuteDeleteAsync(table, arguments, tableId, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Tool '{toolName}' is not exposed by '{Name}'."),
        };
    }

    private static JsonObject ExecuteSelect(DataTable table, JsonElement arguments)
    {
        string? whereColumn = arguments.TryGetProperty("where_column", out var wc) && wc.ValueKind == JsonValueKind.String ? wc.GetString() : null;
        string? whereValue = arguments.TryGetProperty("where_value", out var wv) && wv.ValueKind == JsonValueKind.String ? wv.GetString() : null;

        IReadOnlyList<DataTableRow> rows = !string.IsNullOrWhiteSpace(whereColumn) ? table.SelectWhere(whereColumn, whereValue) : table.SelectAll();
        return SerializeResult(new { rows = rows.Select(r => r.ToDictionary()).ToList(), count = rows.Count });
    }

    private static ValueTask<JsonObject> ExecuteFindAsync(DataTable table, JsonElement arguments)
    {
        if (!TryGetRowId(arguments, out long id, out JsonObject? error))
        {
            return ValueTask.FromResult(error!);
        }

        DataTableRow? row = table.FindById(id);
        return ValueTask.FromResult(row is null ? McpToolResultJson.CreateFailedResult($"No row with id {id} was found.", "row_not_found") : SerializeResult(row.ToDictionary()));
    }

    private async ValueTask<JsonObject> ExecuteInsertAsync(DataTable table, JsonElement arguments, string tableId, CancellationToken ct)
    {
        if (!arguments.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            return McpToolResultJson.CreateFailedResult("The 'data' argument must be a JSON object of column values.", "invalid_arguments");
        }

        DataTableRow row = table.Insert(DataTableRowDataMapping.ConvertToDictionary(dataElement));
        await _rowStore.SaveAsync(tableId, ct).ConfigureAwait(false);
        return SerializeResult(row.ToDictionary());
    }

    private async ValueTask<JsonObject> ExecuteUpdateAsync(DataTable table, JsonElement arguments, string tableId, CancellationToken ct)
    {
        if (!TryGetRowId(arguments, out long id, out JsonObject? error))
        {
            return error!;
        }

        if (!arguments.TryGetProperty("data", out JsonElement dataElement) || dataElement.ValueKind != JsonValueKind.Object)
        {
            return McpToolResultJson.CreateFailedResult("The 'data' argument must be a JSON object of column values.", "invalid_arguments");
        }

        DataTableRow? row = table.Update(id, DataTableRowDataMapping.ConvertToDictionary(dataElement));
        if (row is null)
        {
            return McpToolResultJson.CreateFailedResult($"No row with id {id} was found.", "row_not_found");
        }

        await _rowStore.SaveAsync(tableId, ct).ConfigureAwait(false);
        return SerializeResult(row.ToDictionary());
    }

    private async ValueTask<JsonObject> ExecuteDeleteAsync(DataTable table, JsonElement arguments, string tableId, CancellationToken ct)
    {
        if (!TryGetRowId(arguments, out long id, out JsonObject? error))
        {
            return error!;
        }

        bool success = table.Delete(id);
        if (!success)
        {
            return McpToolResultJson.CreateFailedResult($"No row with id {id} was found.", "row_not_found");
        }

        await _rowStore.SaveAsync(tableId, ct).ConfigureAwait(false);
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
