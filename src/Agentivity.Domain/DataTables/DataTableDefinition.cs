using System.Text.Json.Serialization;

namespace Agentivity.Domain.DataTables;

/// <summary>
/// Definition of a DataTable embedded in a workflow: columns schema and optional initial rows.
/// </summary>
public sealed class DataTableDefinition
{
    public DataTableDefinition()
    {
        Columns = [];
        Rows = [];
    }

    [JsonPropertyName("columns")]
    public IReadOnlyList<DataTableColumn> Columns { get; init; }

    [JsonPropertyName("rows")]
    public IReadOnlyList<Dictionary<string, object?>> Rows { get; init; }
}
