using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Agentivity.Infrastructure.Storage.Sqlite;

/// <summary>
/// A SQLite connection that a query written elsewhere (by an agent, in a workflow) can only read through: SQLite itself is told to
/// refuse anything but reading — no write, no PRAGMA, no ATTACH of another file — and to read only the tables let through. The
/// refusal comes as a <see cref="SqliteException"/> whose <see cref="SqliteException.SqliteErrorCode"/> is <see cref="Refused"/>,
/// whatever the query tries and wherever it tries it (the first statement or a later one).
/// </summary>
public static class SqliteReadOnly
{
    /// <summary>SQLITE_AUTH: the query asked for something it may not do.</summary>
    public const int Refused = 23;

    /// <summary>SQLITE_INTERRUPT: the query ran longer than it may.</summary>
    public const int Stopped = 9;

    private const int Ok = 0;
    private const int Deny = 1;
    private const int Read = 20;
    private const int Select = 21;
    private const int Function = 31;
    private const int Recursive = 33;

    /// <summary>
    /// Lets <paramref name="connection"/> only read: SELECT, functions, recursive WITH, and the tables <paramref name="readable"/>
    /// lets through (it is given the table and its database; a WITH step comes under no database). When a <paramref name="timeout"/>
    /// is given, a query that runs longer is stopped.
    /// </summary>
    public static void Restrict(SqliteConnection connection, Func<string?, string?, bool> readable, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(readable);

        strdelegate_authorizer onlyReading = (object userData, int action, string first, string second, string database, string trigger) =>
            action switch
            {
                Select or Function or Recursive => Ok,
                Read when readable(first, database) => Ok,
                _ => Deny,
            };
        raw.sqlite3_set_authorizer(connection.Handle, onlyReading, null);

        if (timeout is { } limit)
        {
            var clock = Stopwatch.StartNew();
            raw.sqlite3_progress_handler(connection.Handle, 1000, _ => clock.Elapsed > limit ? 1 : 0, null);
        }
    }
}
