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
    /// The table as it was kept: each row with its own id, its own dates and its values, checked against <paramref name="schema"/>.
    /// The next row made gets the id after the highest one. Throws <see cref="InvalidOperationException"/> naming the row when
    /// one does not fit the schema, or when two rows have the same id.
    /// </summary>
    public static DataTable Restore(DataTableSchema schema, IEnumerable<DataTableStoredRow> rows)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);
        var table = new DataTable(schema);
        var seen = new HashSet<long>();
        foreach (var row in rows)
        {
            if (!seen.Add(row.Id))
                throw new InvalidOperationException($"Two rows have the id {row.Id}.");
            var errors = schema.Validate(row.Data);
            if (errors.Count > 0)
                throw new InvalidOperationException($"Row {row.Id} does not fit the schema: {string.Join("; ", errors)}");
            table._rows.Add(new DataTableRow(row.Id, row.CreatedAt, row.UpdatedAt, schema.CoerceValues(row.Data)));
        }

        table._nextId = seen.Count == 0 ? 1 : seen.Max() + 1;
        return table;
    }

    /// <summary>The rows as they are now, to be kept: ids, dates and values.</summary>
    public IReadOnlyList<DataTableStoredRow> Snapshot()
    {
        lock (_lock)
        {
            return _rows.Select(r => new DataTableStoredRow(r.Id, r.CreatedAt, r.UpdatedAt, new Dictionary<string, object?>(r.Data, StringComparer.OrdinalIgnoreCase))).ToList();
        }
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

    /// <summary>
    /// The rows for which every condition of <paramref name="query"/> is true, sorted, one page of them, and how many there are in
    /// all. Throws <see cref="DataTableQueryException"/>, saying why, when the search cannot be done (a column the table does not
    /// have, a value that is not of the column's type, an operator that does not fit it).
    /// </summary>
    public DataTableSearchResult Search(DataTableQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_lock)
        {
            return DataTableSearch.Run(Schema, _rows, query);
        }
    }

    /// <summary>The column of that name as this table spells it (its own columns and <c>id</c>, <c>createdAt</c>, <c>updatedAt</c>), or why there is none.</summary>
    public string ColumnNamed(string name) => DataTableSearch.ColumnNamed(Schema, name);

    /// <summary>Every column a search can use: <c>id</c>, <c>createdAt</c>, <c>updatedAt</c>, then the table's own.</summary>
    public IReadOnlyList<string> SearchableColumns => [.. DataTableSearch.SystemColumns, .. Schema.Columns.Select(c => c.Name)];

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
