using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// Takes over, once for the workspace file, what was kept elsewhere before it (by the JSON workspace modules): <c>what</c> names
/// the part taken over, <c>from</c> where it was kept. What is taken over and the mark that it was are written in one transaction,
/// so a take-over that fails leaves nothing behind and is tried again on the next use; once marked, it never runs again, and what
/// is changed afterwards is never undone. Nothing is removed from where it was taken: it stays there, as a copy.
/// </summary>
internal sealed class SqliteTakeOver(string databasePath, string what, string from, Func<SqliteConnection, SqliteTransaction, Task<int>> takeOver, ILogger logger)
{
    private readonly object _gate = new();
    private Task? _done;

    public Task EnsureAsync()
    {
        lock (_gate)
        {
            if (_done is null || _done.IsFaulted || _done.IsCanceled)
            {
                _done = RunAsync();
            }

            return _done;
        }
    }

    private async Task RunAsync()
    {
        await using var connection = await SqliteWorkspaceDatabase.OpenAsync(databasePath, CancellationToken.None).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using (var known = connection.CreateCommand())
        {
            known.Transaction = transaction;
            known.CommandText = "SELECT COUNT(*) FROM workspace_takeovers WHERE what = $what";
            known.Parameters.AddWithValue("$what", what);
            if (Convert.ToInt64(await known.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        var items = await takeOver(connection, transaction).ConfigureAwait(false);
        await using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT INTO workspace_takeovers (what, taken_from, taken_at, items) VALUES ($what, $from, $at, $items)";
            mark.Parameters.AddWithValue("$what", what);
            mark.Parameters.AddWithValue("$from", from);
            mark.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            mark.Parameters.AddWithValue("$items", items);
            await mark.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        if (items > 0)
        {
            logger.LogInformation("The workspace database {Database} took over {Items} item(s) of {What} from {From}, which was left as it is", databasePath, items, what, from);
        }
    }
}
