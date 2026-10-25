using Agentivity.Domain.DataTables;
using System.Data.Common;
using Agentivity.Infrastructure.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// The Data Tables as real tables of the workspace's SQLite file, beside the rest of the workspace (see
/// <see cref="DatabaseDataTableStore"/>). A write takes the file for itself at once (SQLite's immediate transactions): two changes
/// of one table never cross.
/// </summary>
public sealed class SqliteDataTableStore(string databasePath) : DatabaseDataTableStore
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);

    public override string Describe(string tableId) => $"the workspace database {_databasePath} (table '{tableId}')";

    protected override async Task<DbConnection> OpenAsync(CancellationToken cancellationToken) => await SqliteWorkspaceDatabase.OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);

    protected override string TableName(string physicalName) => Quote(physicalName);

    protected override string SqlType(Holds holds) => holds switch
    {
        Holds.Number => "REAL",
        Holds.YesNo => "INTEGER",
        _ => "TEXT",
    };

    // agentivity_fold: given to each connection (SqliteWorkspaceDatabase), every letter in capitals — SQLite's upper() knows only A to Z.
    protected override string Fold(string sql) => $"agentivity_fold({sql})";

    protected override string Position(string haystack, string needle) => $"instr({haystack}, {needle})";

    protected override string Trim(string sql) => $"trim({sql}, ' ' || char(9) || char(10) || char(13))";

    protected override string JsonText(string sql) => $"json_extract({sql}, '$')";

    protected override string ListItems(string sql) => $"json_each({sql})";

    protected override string ListLength(string sql) => $"json_array_length({sql})";

    // SQLite finds a parameter by its name among all those of the statement: a row per statement, the same statement each time,
    // writes many rows fastest.
    protected override int RowsPerStatement(int parametersPerRow) => 1;

    // AUTOINCREMENT: an id is never given twice, not even the id of the last row once it is deleted.
    protected override string IdColumnDefinition => "INTEGER PRIMARY KEY AUTOINCREMENT";

    public override string SqlDialect => "SQLite";

    protected override string QueryNotes =>
        "Yes/no columns are 1 or 0, list and structured columns are JSON text (read them with json_each or json_extract), dates are ISO 8601 text.";

    // Table-valued functions a query may read like tables: they read JSON (a list kept in a row), nothing else.
    private static readonly HashSet<string> JsonTables = new(StringComparer.OrdinalIgnoreCase) { "json_each", "json_tree" };

    /// <summary>
    /// The query runs on a connection of its own that can only read the file (SQLite's read-only mode), where the table is a view
    /// named as the agent knows it; and SQLite is told to refuse anything but reading that table (no other table — the rest of the
    /// workspace, another Data Table, SQLite's own —, no PRAGMA, no ATTACH), whatever statement asks it, and to stop the query after
    /// <paramref name="timeout"/>.
    /// </summary>
    public override async ValueTask<DataTableQueryResult> QueryAsync(DataTableSchema schema, string tableName, string sql, int? maxRows, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        var text = OneSelect(sql, tableName);
        var physical = PhysicalName(schema.TableName);

        // The file and its tables made, if this is the first thing asked of it.
        await (await OpenAsync(cancellationToken).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var view = connection.CreateCommand())
        {
            view.CommandText = $"CREATE TEMP VIEW {Quote(tableName)} AS SELECT * FROM main.{Quote(physical)}";
            await view.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // One statement: SQLite says what follows the first one, which must be nothing.
        if (raw.sqlite3_prepare_v2(connection.Handle, text, out var first, out string rest) == raw.SQLITE_OK)
        {
            first.Dispose();
            if (!string.IsNullOrWhiteSpace(rest?.Trim().TrimEnd(';')))
                throw new DataTableQueryException(OnlyReads(tableName));
        }

        SqliteReadOnly.Restrict(
            connection,
            (table, database) =>
                (string.Equals(table, physical, StringComparison.Ordinal) && string.Equals(database, "main", StringComparison.OrdinalIgnoreCase))
                || (string.Equals(table, tableName, StringComparison.OrdinalIgnoreCase) && string.Equals(database, "temp", StringComparison.OrdinalIgnoreCase))
                || JsonTables.Contains(table ?? string.Empty)
                // What the query names itself (a WITH step) is read under no database.
                || string.IsNullOrEmpty(database),
            timeout);

        try
        {
            List<string> columns;
            List<Dictionary<string, object?>> rows;
            bool more;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = text;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                (columns, rows, more) = await ReadAnswerAsync(reader, maxRows, cancellationToken).ConfigureAwait(false);
                // What follows the first statement is still run when the reader closes: the guard refuses it all the same.
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) { }
            }

            int? total = null;
            if (more)
            {
                await using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM ({text})";
                total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            }

            return new DataTableQueryResult(columns, rows, total);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteReadOnly.Refused)
        {
            throw new DataTableQueryException(OnlyReads(tableName));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteReadOnly.Stopped)
        {
            throw new DataTableQueryException(TooLong(timeout));
        }
        catch (SqliteException ex)
        {
            throw new DataTableQueryException($"The query cannot be run: {ex.Message.Replace("SQLite Error 1: ", string.Empty, StringComparison.Ordinal)}");
        }
    }
}
