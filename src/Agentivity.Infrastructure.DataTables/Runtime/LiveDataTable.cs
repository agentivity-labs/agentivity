using System.Runtime.CompilerServices;
using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Infrastructure.DataTables.Runtime;

/// <summary>
/// An open Data Table: a proxy for its real table in the workspace's database (see <see cref="IDataTableStore"/>). Every row
/// written is written there at once, every search is done there; nothing of the table is held here but its schema.
/// </summary>
public sealed class LiveDataTable(IDataTableStore store, DataTableSchema schema)
{
    public DataTableSchema Schema { get; } = schema ?? throw new ArgumentNullException(nameof(schema));

    /// <summary>The id of the table.</summary>
    public string Id => Schema.TableName;

    /// <summary>Every column a search can use: <c>id</c>, <c>createdAt</c>, <c>updatedAt</c>, then the table's own.</summary>
    public IReadOnlyList<string> SearchableColumns => [.. DataTableSearchPlan.SystemColumns, .. Schema.Columns.Select(c => c.Name)];

    /// <summary>The column of that name as this table spells it, or why there is none (<see cref="DataTableQueryException"/>).</summary>
    public string ColumnNamed(string name) => DataTableSearchPlan.ColumnNamed(Schema, name);

    /// <summary>A new row. Throws <see cref="InvalidOperationException"/> when it does not fit the schema: nothing is written.</summary>
    public ValueTask<DataTableRow> InsertAsync(IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) => store.InsertAsync(Schema, values, cancellationToken);

    /// <summary>Changes the columns named in <paramref name="updates"/>; null when there is no row of that id.</summary>
    public ValueTask<DataTableRow?> UpdateAsync(long id, IReadOnlyDictionary<string, object?> updates, CancellationToken cancellationToken = default) => store.UpdateAsync(Schema, id, updates, cancellationToken);

    /// <summary>Removes the row of that id; false when there is none.</summary>
    public ValueTask<bool> DeleteAsync(long id, CancellationToken cancellationToken = default) => store.DeleteRowAsync(Id, id, cancellationToken);

    public ValueTask<DataTableRow?> FindAsync(long id, CancellationToken cancellationToken = default) => store.FindAsync(Schema, id, cancellationToken);

    /// <summary>What <paramref name="query"/> finds: one page at most (<see cref="IDataTableStore.MaxPageSize"/>), and the total.</summary>
    public ValueTask<DataTableSearchResult> SearchAsync(DataTableQuery query, CancellationToken cancellationToken = default) => store.SearchAsync(Schema, query, cancellationToken);

    /// <summary>The SQL a query on the table is written in (<c>SQLite</c>, <c>PostgreSQL</c>).</summary>
    public string SqlDialect => store.SqlDialect;

    /// <summary>The table as a query that names it <paramref name="tableName"/> sees it: its columns, their SQL types, how its values are read.</summary>
    public string DescribeForQuery(string tableName) => store.DescribeForQuery(Schema, tableName);

    /// <summary>
    /// One read-only SQL SELECT, written by an agent that names the table <paramref name="tableName"/>, done by the database on the
    /// table itself and on nothing else (see <see cref="IDataTableStore.QueryAsync"/>).
    /// </summary>
    public ValueTask<DataTableQueryResult> QueryAsync(string tableName, string sql, int? maxRows, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        store.QueryAsync(Schema, tableName, sql, maxRows, timeout, cancellationToken);

    /// <summary>
    /// Every row, a page at a time, in the order of their ids — for what needs them all, a template made of the table: never held
    /// together here.
    /// </summary>
    public async IAsyncEnumerable<DataTableRow> RowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var offset = 0; ; offset += IDataTableStore.MaxPageSize)
        {
            var page = await store.PageAsync(Schema, offset, IDataTableStore.MaxPageSize, cancellationToken).ConfigureAwait(false);
            foreach (var row in page.Rows)
                yield return row;
            if (page.Rows.Count < IDataTableStore.MaxPageSize)
                yield break;
        }
    }
}
