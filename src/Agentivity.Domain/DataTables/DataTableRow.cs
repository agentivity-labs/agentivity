namespace Agentivity.Domain.DataTables;

/// <summary>
/// Represents a single row in a DataTable with system columns (id, createdAt, updatedAt).
/// </summary>
public sealed class DataTableRow
{
    private readonly Dictionary<string, object?> _data;

    public long Id { get; }
    public DateTime CreatedAt { get; }
    public DateTime UpdatedAt { get; private set; }
    public IReadOnlyDictionary<string, object?> Data => _data;

    /// <summary>The same row under a new schema: its id, its dates and its values are kept.</summary>
    internal DataTableRow(long id, DateTime createdAt, DateTime updatedAt, Dictionary<string, object?> data)
    {
        Id = id;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        _data = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A row as it is kept where the table is (its id, its dates, its values), read back.</summary>
    public static DataTableRow Kept(long id, DateTime createdAt, DateTime updatedAt, IReadOnlyDictionary<string, object?> data) =>
        new(id, createdAt, updatedAt, new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase));

    internal DataTableRow(long id, Dictionary<string, object?> data)
    {
        Id = id;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = CreatedAt;
        _data = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
    }

    public T? Get<T>(string column)
    {
        if (!_data.TryGetValue(column, out var value) || value is null)
            return default;
        if (value is T typed)
            return typed;
        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    public object? Get(string column) => _data.TryGetValue(column, out var value) ? value : null;

    internal void Update(IReadOnlyDictionary<string, object?> updates)
    {
        foreach (var (key, value) in updates)
            _data[key] = value;
        UpdatedAt = DateTime.UtcNow;
    }

    public Dictionary<string, object?> ToDictionary()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = Id,
            ["createdAt"] = CreatedAt,
            ["updatedAt"] = UpdatedAt,
        };
        foreach (var (key, value) in _data)
            result[key] = value;
        return result;
    }
}
