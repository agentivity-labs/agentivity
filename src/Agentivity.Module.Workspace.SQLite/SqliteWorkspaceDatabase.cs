using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// The workspace's SQLite file: how it is opened, and its tables, made and kept up to date by its migrations
/// (<see cref="SqliteWorkspaceMigrations"/>) the first time the file is opened by this process.
/// <list type="bullet">
/// <item><c>workspace_folders</c>, <c>workspace_objects</c>: the objects of each domain and their folders.</item>
/// <item><c>datatable_tables</c>: the Data Tables whose rows are kept (a table kept without rows is still one).</item>
/// <item><c>datatable_rows</c>: their rows — id, dates, and the values as JSON, so that a change of columns needs no change here.</item>
/// <item><c>datatable_schemas</c>: each Data Table made a real table of the database — its plain name, and its columns and what each holds (see <see cref="Agentivity.Infrastructure.Storage.Workspace.DatabaseDataTableStore"/>).</item>
/// <item><c>knowledge_documents</c>, <c>knowledge_passages</c>, <c>knowledge_states</c>: the documents, passages (with their vectors) and last synchronisation of each Knowledge Base (see <see cref="Agentivity.Infrastructure.Storage.Workspace.DatabaseKnowledgeBaseStore"/>).</item>
/// <item><c>workspace_takeovers</c>: what was taken over from where it was kept before (see <see cref="SqliteTakeOver"/>).</item>
/// <item><c>schema_migrations</c>: the migrations the file went through.</item>
/// </list>
/// </summary>
internal static class SqliteWorkspaceDatabase
{
    private static readonly ConcurrentDictionary<string, Lazy<Task>> Prepared = new(StringComparer.OrdinalIgnoreCase);

    // A write that finds the file busy waits for it (up to the timeout) instead of failing at once.
    public static string ConnectionString(string databasePath) => new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true, DefaultTimeout = 30 }.ToString();

    /// <summary>Lets go of the connections kept open (pooled) to the file at <paramref name="databasePath"/>, and only to it.</summary>
    public static void Release(string databasePath) => SqliteConnection.ClearPool(new SqliteConnection(ConnectionString(Path.GetFullPath(databasePath))));

    public static async Task<SqliteConnection> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        await PrepareAsync(databasePath).ConfigureAwait(false);
        var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Names are listed in the order the other workspace modules list them (SQLite's NOCASE folds to lower case, and only ASCII).
        connection.CreateCollation("WORKSPACE_NAME", (x, y) => StringComparer.OrdinalIgnoreCase.Compare(x, y));
        // Every letter in capitals, accents included (SQLite's upper() knows only A to Z): the searches of the Data Tables compare
        // texts without regard to case with it.
        connection.CreateFunction("agentivity_fold", (string? text) => text?.ToUpperInvariant(), isDeterministic: true);
        return connection;
    }

    /// <summary>
    /// Brings the file to the last of its migrations, once for this process: at the start of the server
    /// (<see cref="SqliteWorkspaceMigrationService"/>), or at its first use if that comes first.
    /// </summary>
    public static Task PrepareAsync(string databasePath, ILogger? logger = null) =>
        Prepared.GetOrAdd(databasePath, path => new Lazy<Task>(() => SqliteWorkspaceMigrations.MigrateAsync(path, logger ?? NullLogger.Instance))).Value;
}
