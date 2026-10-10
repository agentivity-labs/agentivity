namespace Agentivity.Domain.DataTables;

/// <summary>
/// An in-memory table with schema validation and CRUD operations.
/// </summary>
public sealed class DataTable
{
    private readonly List<DataTableRow> _rows = [];
    private readonly object _lock = new();
    private long _nextId = 1;

    public DataTableSchema Schema { get; }
    public string Name => Schema.TableName;
    public int Count => _rows.Count;

    public DataTable(DataTableSchema schema)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public DataTable(DataTableSchema schema, IEnumerable<IReadOnlyDictionary<string, object?>> initialRows)
        : this(schema)
    {
        foreach (var row in initialRows)
            Insert(row);
    }

    /// <summary>
    /// This table's rows under a new schema (a column added, retyped or made optional): same ids, same dates, same values. Throws
    /// <see cref="InvalidOperationException"/> naming the row when one does not fit the new schema — the table is then left as it was.
    /// </summary>
    public DataTable WithSchema(DataTableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_lock)
        {
            var next = new DataTable(schema) { _nextId = _nextId };
            foreach (var row in _rows)
            {
                var data = new Dictionary<string, object?>(row.Data, StringComparer.OrdinalIgnoreCase);
                var errors = schema.Validate(data);
                if (errors.Count > 0)
                    throw new InvalidOperationException($"Row {row.Id} does not fit the new schema: {string.Join("; ", errors)}");
                next._rows.Add(new DataTableRow(row.Id, row.CreatedAt, row.UpdatedAt, schema.CoerceValues(data)));
            }

            return next;
        }
    }

    /// <summary>Inserts a new row. Throws if validation fails.</summary>
    public DataTableRow Insert(IReadOnlyDictionary<string, object?> data)
    {
        var errors = Schema.Validate(data);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Validation failed: {string.Join("; ", errors)}");

        var coerced = Schema.CoerceValues(data);
        lock (_lock)
        {
            var row = new DataTableRow(_nextId++, coerced);
            _rows.Add(row);
            return row;
        }
    }

    /// <summary>Updates an existing row by id. Returns null if not found.</summary>
    public DataTableRow? Update(long id, IReadOnlyDictionary<string, object?> updates)
    {
        lock (_lock)
        {
            var row = _rows.FirstOrDefault(r => r.Id == id);
            if (row is null)
                return null;

            var merged = new Dictionary<string, object?>(row.Data, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in updates)
                merged[key] = value;

            var errors = Schema.Validate(merged);
            if (errors.Count > 0)
                throw new InvalidOperationException($"Validation failed: {string.Join("; ", errors)}");

            row.Update(Schema.CoerceValues(updates));
            return row;
        }
    }

    /// <summary>Deletes a row by id. Returns true if deleted.</summary>
    public bool Delete(long id)
    {
        lock (_lock)
        {
            var row = _rows.FirstOrDefault(r => r.Id == id);
            if (row is null)
                return false;
            _rows.Remove(row);
            return true;
        }
    }

    public DataTableRow? FindById(long id)
    {
        lock (_lock)
        {
            return _rows.FirstOrDefault(r => r.Id == id);
        }
    }

    public IReadOnlyList<DataTableRow> SelectAll()
    {
        lock (_lock)
        {
            return _rows.ToList();
        }
    }

    public IReadOnlyList<DataTableRow> Select(Func<DataTableRow, bool> predicate)
    {
        lock (_lock)
        {
            return _rows.Where(predicate).ToList();
        }
    }

    public IReadOnlyList<DataTableRow> SelectWhere(string column, object? value)
    {
        lock (_lock)
        {
            return _rows.Where(r => Equals(r.Get(column), value)).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _rows.Clear();
            _nextId = 1;
        }
    }

    /// <summary>Converts all rows to dictionaries (for expression access).</summary>
    public IReadOnlyList<Dictionary<string, object?>> ToList()
    {
        lock (_lock)
        {
            return _rows.Select(r => r.ToDictionary()).ToList();
        }
    }
}
