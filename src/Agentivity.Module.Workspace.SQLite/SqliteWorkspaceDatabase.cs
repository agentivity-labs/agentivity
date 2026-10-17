using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// The workspace's SQLite file: how it is opened, and its tables, made the first time the file is opened by this process.
/// <list type="bullet">
/// <item><c>workspace_folders</c>, <c>workspace_objects</c>: the objects of each domain and their folders.</item>
/// <item><c>datatable_tables</c>: the Data Tables whose rows are kept (a table kept without rows is still one).</item>
/// <item><c>datatable_rows</c>: their rows — id, dates, and the values as JSON, so that a change of columns needs no change here.</item>
/// <item><c>datatable_schemas</c>: each Data Table made a real table of the database — its plain name, and its columns and what each holds (see <see cref="Agentivity.Infrastructure.Storage.Workspace.DatabaseDataTableStore"/>).</item>
/// <item><c>workspace_takeovers</c>: what was taken over from where it was kept before (see <see cref="SqliteTakeOver"/>).</item>
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
        await Prepared.GetOrAdd(databasePath, path => new Lazy<Task>(() => PrepareAsync(path))).Value.ConfigureAwait(false);
        var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Names are listed in the order the other workspace modules list them (SQLite's NOCASE folds to lower case, and only ASCII).
        connection.CreateCollation("WORKSPACE_NAME", (x, y) => StringComparer.OrdinalIgnoreCase.Compare(x, y));
        // Every letter in capitals, accents included (SQLite's upper() knows only A to Z): the searches of the Data Tables compare
        // texts without regard to case with it.
        connection.CreateFunction("agentivity_fold", (string? text) => text?.ToUpperInvariant(), isDeterministic: true);
        return connection;
    }

    private static async Task PrepareAsync(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // WAL: readers do not wait for the writer, and the writer does not wait for readers.
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS workspace_folders (
                domain TEXT NOT NULL,
                folder_id TEXT NOT NULL,
                name TEXT NOT NULL,
                parent_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (domain, folder_id)
            );
            CREATE TABLE IF NOT EXISTS workspace_objects (
                domain TEXT NOT NULL,
                object_type TEXT NOT NULL COLLATE NOCASE,
                object_id TEXT NOT NULL,
                name TEXT NOT NULL,
                payload TEXT NOT NULL,
                content_type TEXT NOT NULL,
                folder_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                created_by TEXT NULL,
                updated_by TEXT NULL,
                tags TEXT NULL,
                PRIMARY KEY (domain, object_type, object_id)
            );
            CREATE INDEX IF NOT EXISTS workspace_objects_by_folder ON workspace_objects (domain, object_type, folder_id);
            CREATE INDEX IF NOT EXISTS workspace_folders_by_parent ON workspace_folders (domain, parent_id);
            CREATE TABLE IF NOT EXISTS datatable_tables (
                table_id TEXT NOT NULL PRIMARY KEY,
                saved_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS datatable_rows (
                table_id TEXT NOT NULL,
                row_id INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                data TEXT NOT NULL,
                PRIMARY KEY (table_id, row_id)
            );
            CREATE TABLE IF NOT EXISTS datatable_schemas (
                table_id TEXT NOT NULL PRIMARY KEY,
                physical_name TEXT NOT NULL,
                columns TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS workspace_takeovers (
                what TEXT NOT NULL PRIMARY KEY,
                taken_from TEXT NOT NULL,
                taken_at TEXT NOT NULL,
                items INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
