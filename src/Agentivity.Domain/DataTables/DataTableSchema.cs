namespace Agentivity.Domain.DataTables;

/// <summary>
/// Defines the schema for a DataTable: column index, validation, and coercion.
/// </summary>
public sealed class DataTableSchema
{
    private readonly Dictionary<string, DataTableColumn> _columns;

    public string TableName { get; }
    public IReadOnlyList<DataTableColumn> Columns => _columns.Values.ToList();

    public DataTableSchema(string tableName, IEnumerable<DataTableColumn> columns)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or whitespace.", nameof(tableName));

        TableName = tableName;
        _columns = new Dictionary<string, DataTableColumn>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            if (_columns.ContainsKey(column.Name))
                throw new ArgumentException($"Duplicate column name: {column.Name}", nameof(columns));
            _columns[column.Name] = column;
        }

        // No column is a schema too: a table that has just been created has none yet, and gets its first one afterwards.
    }

    public DataTableColumn? GetColumn(string name) => _columns.TryGetValue(name, out var col) ? col : null;

    public bool HasColumn(string name) => _columns.ContainsKey(name);

    /// <summary>Validates a data dictionary against the schema. Returns a list of errors (empty if valid).</summary>
    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, object?> data)
    {
        var errors = new List<string>();

        foreach (var column in _columns.Values)
        {
            if (column.Required)
            {
                var hasValue = data.TryGetValue(column.Name, out var value) && value is not null;
                if (!hasValue && column.Default is null)
                    errors.Add($"Required column '{column.Name}' is missing or null.");
            }
        }

        foreach (var (key, value) in data)
        {
            if (!_columns.TryGetValue(key, out var col))
                continue; // flexible schema
            if (!col.IsValidValue(value))
                errors.Add($"Column '{col.Name}' has invalid value for type {col.Type}.");
        }

        return errors;
    }

    /// <summary>Coerces all values in the data dictionary to their appropriate types.</summary>
    public Dictionary<string, object?> CoerceValues(IReadOnlyDictionary<string, object?> data)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in _columns.Values)
        {
            if (data.TryGetValue(column.Name, out var value))
                result[column.Name] = column.CoerceValue(value);
            else if (column.Default is not null)
                result[column.Name] = column.Default;
        }

        return result;
    }
}
