using Agentivity.Domain.DataTables;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The Data Tables of the workspace, each a real table of the workspace's database (SQLite, Postgres): one column per column of
/// the table, plus <c>id</c>, <c>createdAt</c> and <c>updatedAt</c>. Rows are written, read and changed one at a time, in the
/// database: a table is never read whole. The values are checked and converted by the schema (<see cref="DataTableSchema"/>)
/// before they are written, so a column holds values of its type. The names of the table and of its columns are only ever
/// written by this store, quoted; values are always parameters.
/// </summary>
public interface IDataTableStore
{
    /// <summary>The most rows one page gives, however many are asked for.</summary>
    public const int MaxPageSize = 1000;

    /// <summary>Where the rows of <paramref name="tableId"/> are kept, in words a person can look for: for messages.</summary>
    string Describe(string tableId);

    /// <summary>
    /// Makes the table of <see cref="DataTableSchema.TableName"/> fit <paramref name="schema"/>: made when it does not exist yet —
    /// with <paramref name="rowsWhenMade"/>, rows kept before elsewhere, in the same transaction —,
    /// columns added (the rows there get the column's default), removed (with their values), renamed when only their case
    /// changes, converted when their type changes. Done all at once: when a row does not fit the new schema — a value that cannot
    /// be converted, no value for a column now required — nothing is changed, and <see cref="DataTableRowsDoNotFitException"/> says
    /// which row and why. When the table already fits, nothing is written.
    /// </summary>
    ValueTask ApplySchemaAsync(DataTableSchema schema, IReadOnlyList<DataTableStoredRow>? rowsWhenMade = null, CancellationToken cancellationToken = default);

    /// <summary>Why the rows would not fit <paramref name="schema"/>, or null when they all do (the table is left as it is).</summary>
    ValueTask<string?> WhyRowsDoNotFitAsync(DataTableSchema schema, CancellationToken cancellationToken = default);

    /// <summary>A new row: the next id, made and changed now. Throws <see cref="InvalidOperationException"/> when it does not fit the schema.</summary>
    ValueTask<DataTableRow> InsertAsync(DataTableSchema schema, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);

    /// <summary>Rows as they were kept elsewhere, with their ids and dates (a take-over, a template); the next id made is after them.</summary>
    ValueTask InsertKeptAsync(DataTableSchema schema, IReadOnlyList<DataTableStoredRow> rows, CancellationToken cancellationToken = default);

    /// <summary>The row of that id, or null.</summary>
    ValueTask<DataTableRow?> FindAsync(DataTableSchema schema, long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the columns named in <paramref name="updates"/> of the row of that id (the others are left as they are), or null when
    /// there is none. Throws <see cref="InvalidOperationException"/> when the row would not fit the schema: nothing is changed.
    /// </summary>
    ValueTask<DataTableRow?> UpdateAsync(DataTableSchema schema, long id, IReadOnlyDictionary<string, object?> updates, CancellationToken cancellationToken = default);

    /// <summary>Removes the row of that id; false when there is none.</summary>
    ValueTask<bool> DeleteRowAsync(string tableId, long id, CancellationToken cancellationToken = default);

    /// <summary>One page of the rows, in the order of their ids (at most <see cref="MaxPageSize"/>), and how many rows there are in all.</summary>
    ValueTask<DataTablePage> PageAsync(DataTableSchema schema, int offset, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rows that <paramref name="query"/> finds, searched in the database (see <see cref="DataTableSearchPlan"/> for what it finds),
    /// one page of them — at most <see cref="MaxPageSize"/>, however many are asked for, or none asked for — and how many it finds
    /// in all. Throws <see cref="DataTableQueryException"/>, saying why, when the search cannot be done.
    /// </summary>
    ValueTask<DataTableSearchResult> SearchAsync(DataTableSchema schema, DataTableQuery query, CancellationToken cancellationToken = default);

    /// <summary>The SQL a query is written in, for the agent that writes it: <c>SQLite</c> or <c>PostgreSQL</c>.</summary>
    string SqlDialect { get; }

    /// <summary>
    /// The table as a query sees it, for the agent that writes one: <c>"name"("column" TYPE, …)</c>, then how its values are read in
    /// <see cref="SqlDialect"/> (yes/no, lists and structured values, dates).
    /// </summary>
    string DescribeForQuery(DataTableSchema schema, string tableName);

    /// <summary>
    /// One read-only SQL SELECT on the table, written by an agent, which names it <paramref name="tableName"/>: done by the
    /// database, on the real table. The query reads that table and nothing else — not the rest of the workspace, not another Data
    /// Table —, writes nothing, is one statement, and is stopped after <paramref name="timeout"/>. At most
    /// <paramref name="maxRows"/> rows come back, with the total when there are more. Anything else — refused, wrong, too long —
    /// throws <see cref="DataTableQueryException"/>, saying why in words the agent can correct its query with; nothing is changed.
    /// </summary>
    ValueTask<DataTableQueryResult> QueryAsync(DataTableSchema schema, string tableName, string sql, int? maxRows, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>How many rows have a value in the column <paramref name="column"/> (0 when the table has not been made).</summary>
    ValueTask<long> CountValuesAsync(DataTableSchema schema, string column, CancellationToken cancellationToken = default);

    /// <summary>Whether the table has been made.</summary>
    ValueTask<bool> ExistsAsync(string tableId, CancellationToken cancellationToken = default);

    /// <summary>The table is deleted, with its rows.</summary>
    ValueTask DropAsync(string tableId, CancellationToken cancellationToken = default);
}

/// <summary>The answer to a SQL query: its columns, at most the rows asked for, and — when it gave more — how many it gave.</summary>
public sealed record DataTableQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, int? Total);

/// <summary>One page of the rows of a table, and how many rows the table has.</summary>
public sealed record DataTablePage(IReadOnlyList<DataTableRow> Rows, long Total);

/// <summary>The rows of a table do not fit a new schema: the message names the row and says why. Nothing was changed.</summary>
public sealed class DataTableRowsDoNotFitException(string why) : InvalidOperationException(why);
