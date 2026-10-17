using System.Globalization;
using System.Text.Json;
using Agentivity.Domain.DataTables;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// The rows of the Data Tables in the workspace's SQLite file, with the rest of the workspace: one row of <c>datatable_rows</c>
/// per row of a table, its values as JSON (they keep their types: text, number, yes/no, list…). Saving a table replaces its rows
/// all at once, in one transaction: a reader sees the rows before or after, never half of them.
/// </summary>
public sealed class SqliteTableRowsStorage : ITableRowsStorage
{
    private readonly string _databasePath;
    private readonly ILogger<SqliteTableRowsStorage> _logger;
    private readonly FileTableRowsStorage? _takeOverFrom;
    private readonly SqliteTakeOver? _takeOver;

    /// <param name="takeOverFrom">
    /// Where the rows were kept before, in files: they are taken over, with their ids and dates, the first time the rows are used
    /// (see <see cref="SqliteTakeOver"/>).
    /// </param>
    public SqliteTableRowsStorage(string databasePath, ILogger<SqliteTableRowsStorage> logger, FileTableRowsStorage? takeOverFrom = null)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _logger = logger;
        _takeOverFrom = takeOverFrom;
        if (takeOverFrom is not null)
        {
            _takeOver = new SqliteTakeOver(_databasePath, "datatable-rows", takeOverFrom.Location, TakeOverFilesAsync, logger);
        }
    }

    /// <summary>Where the rows kept before this storage are taken over from.</summary>
    public string? TakesOverFrom => _takeOverFrom?.Location;

    public string Describe(string tableId) => $"the workspace database {_databasePath} (table '{tableId}')";

    public async ValueTask<IReadOnlyList<DataTableStoredRow>?> LoadAsync(string tableId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsKeptAsync(connection, null, tableId).ConfigureAwait(false))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_id, created_at, updated_at, data FROM datatable_rows WHERE table_id = $table ORDER BY row_id";
        command.Parameters.AddWithValue("$table", tableId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<DataTableStoredRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            Dictionary<string, object?> data;
            try
            {
                data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(3))?.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase) ?? [];
            }
            catch (JsonException ex)
            {
                throw new TableRowsUnreadableException($"the values of row {id} are not valid JSON.", ex);
            }

            rows.Add(new DataTableStoredRow(id, Date(reader.GetString(1)), Date(reader.GetString(2)), data));
        }

        return rows;
    }

    public async ValueTask SaveAsync(string tableId, IReadOnlyList<DataTableStoredRow> rows, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(connection, transaction, tableId, rows, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAsync(SqliteConnection connection, SqliteTransaction transaction, string tableId, IReadOnlyList<DataTableStoredRow> rows, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM datatable_rows WHERE table_id = $table", tableId, cancellationToken).ConfigureAwait(false);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO datatable_rows (table_id, row_id, created_at, updated_at, data) VALUES ($table, $id, $created, $updated, $data)";
            insert.Parameters.AddWithValue("$table", tableId);
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            var created = insert.Parameters.Add("$created", SqliteType.Text);
            var updated = insert.Parameters.Add("$updated", SqliteType.Text);
            var data = insert.Parameters.Add("$data", SqliteType.Text);
            foreach (var row in rows)
            {
                id.Value = row.Id;
                created.Value = Date(row.CreatedAt);
                updated.Value = Date(row.UpdatedAt);
                data.Value = JsonSerializer.Serialize(row.Data);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO datatable_tables (table_id, saved_at) VALUES ($table, $now) ON CONFLICT (table_id) DO UPDATE SET saved_at = excluded.saved_at";
            mark.Parameters.AddWithValue("$table", tableId);
            mark.Parameters.AddWithValue("$now", Date(DateTime.UtcNow));
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DeleteAsync(string tableId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removed = await ExecuteAsync(connection, transaction, "DELETE FROM datatable_rows WHERE table_id = $table", tableId, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM datatable_tables WHERE table_id = $table", tableId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("The {Count} rows of Data Table {TableId} were deleted with it", removed, tableId);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (_takeOver is not null)
        {
            await _takeOver.EnsureAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await SqliteWorkspaceDatabase.OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
    }

    // Every table kept in a file, all or nothing: a file that cannot be read stops the take-over, which is tried again on the next
    // use, and says which file it is.
    private async Task<int> TakeOverFilesAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        var tables = 0;
        foreach (var tableId in _takeOverFrom!.KeptTables())
        {
            IReadOnlyList<DataTableStoredRow>? rows;
            try
            {
                rows = await _takeOverFrom.LoadAsync(tableId).ConfigureAwait(false);
            }
            catch (TableRowsUnreadableException ex)
            {
                throw new TableRowsUnreadableException($"the rows kept before in {_takeOverFrom.Describe(tableId)} could not be taken over: {ex.Message} That file was left as it is.", ex);
            }

            if (rows is not null && !await IsKeptAsync(connection, transaction, tableId).ConfigureAwait(false))
            {
                await WriteAsync(connection, transaction, tableId, rows, CancellationToken.None).ConfigureAwait(false);
                tables++;
            }
        }

        return tables;
    }

    private static async Task<bool> IsKeptAsync(SqliteConnection connection, SqliteTransaction? transaction, string tableId)
    {
        await using var known = connection.CreateCommand();
        known.Transaction = transaction;
        known.CommandText = "SELECT COUNT(*) FROM datatable_tables WHERE table_id = $table";
        known.Parameters.AddWithValue("$table", tableId);
        return Convert.ToInt64(await known.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, string tableId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$table", tableId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Date(DateTime value) => DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Date(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
