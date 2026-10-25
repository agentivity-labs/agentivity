using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Infrastructure.Storage.Workspace.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agentivity.Module.Workspace.SQLite;

/// <summary>
/// The migrations of the workspace's SQLite file, in order (see <see cref="WorkspaceMigrator"/>). Version 1 is the file as it was
/// made before migrations existed: on such a file it only makes what is missing. A change of the tables, or of what the objects
/// hold, is a new version at the end of <see cref="All"/>, never a change of one already released. Before the first migration to
/// pass on a workspace that already holds something, the file is copied into <c>backups</c> beside it.
/// </summary>
public static class SqliteWorkspaceMigrations
{
    /// <summary>Where the file keeps what its objects hold (it keeps no versions of them).</summary>
    private static readonly IReadOnlyList<WorkspaceContentColumn> Content = [new("workspace_objects", "payload", ["domain", "object_type", "object_id"])];

    public static IReadOnlyList<WorkspaceMigration> All { get; } =
    [
        WorkspaceMigration.Sql(1, "The tables of the workspace", """
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
                CREATE TABLE IF NOT EXISTS knowledge_documents (
                    base_id TEXT NOT NULL,
                    doc_key TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,
                    title TEXT NULL,
                    source TEXT NULL,
                    synced_at TEXT NOT NULL,
                    PRIMARY KEY (base_id, doc_key)
                );
                CREATE TABLE IF NOT EXISTS knowledge_passages (
                    base_id TEXT NOT NULL,
                    doc_key TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    vector BLOB NOT NULL,
                    metadata TEXT NULL,
                    PRIMARY KEY (base_id, doc_key, ordinal)
                );
                CREATE TABLE IF NOT EXISTS knowledge_states (
                    base_id TEXT NOT NULL PRIMARY KEY,
                    state TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS workspace_takeovers (
                    what TEXT NOT NULL PRIMARY KEY,
                    taken_from TEXT NOT NULL,
                    taken_at TEXT NOT NULL,
                    items INTEGER NOT NULL
                );
                """),
        WorkspaceContentMigration.Rewrite(2, WorkspaceContentRewrites.StepRenames20261018Name, Content, WorkspaceContentRewrites.StepRenames20261018Holds, WorkspaceContentRewrites.StepRenames20261018),
        // Every object and folder belongs to a space; what was there goes in the default one. Saving an object again keeps its space.
        WorkspaceMigration.Sql(3, WorkspaceSpaces.MigrationName, """
                ALTER TABLE workspace_objects ADD COLUMN space_id TEXT NOT NULL DEFAULT 'default';
                ALTER TABLE workspace_folders ADD COLUMN space_id TEXT NOT NULL DEFAULT 'default';
                """),
    ];

    /// <summary>
    /// Brings the file at <paramref name="databasePath"/> to the last of <paramref name="migrations"/> (by default <see cref="All"/>),
    /// making it when it does not exist; returns the migrations it passed.
    /// </summary>
    public static async Task<IReadOnlyList<WorkspaceMigration>> MigrateAsync(string databasePath, ILogger logger, IReadOnlyList<WorkspaceMigration>? migrations = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var holdsSomething = File.Exists(databasePath) && await HoldsAWorkspaceAsync(databasePath, cancellationToken).ConfigureAwait(false);
        var target = new WorkspaceMigrationTarget
        {
            OpenAsync = async ct =>
            {
                var connection = new SqliteConnection(SqliteWorkspaceDatabase.ConnectionString(databasePath));
                await connection.OpenAsync(ct).ConfigureAwait(false);
                // WAL: readers do not wait for the writer, and the writer does not wait for readers (set outside a transaction).
                await using var wal = connection.CreateCommand();
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return connection;
            },
            BeforeMigratingAsync = holdsSomething ? (pending, ct) => BackUpAsync(databasePath, pending[0].Version, logger, ct) : null,
        };
        return await WorkspaceMigrator.MigrateAsync(target, migrations ?? All, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The folder the copies made before a migration go to: <c>backups</c>, beside the file.</summary>
    public static string BackupFolderOf(string databasePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".", "backups");

    private static async Task<bool> HoldsAWorkspaceAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(SqliteWorkspaceDatabase.ConnectionString(databasePath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'workspace_objects'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    // VACUUM INTO writes a whole, consistent copy, whatever is in the WAL.
    private static async Task BackUpAsync(string databasePath, int firstVersion, ILogger logger, CancellationToken cancellationToken)
    {
        var folder = BackupFolderOf(databasePath);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(databasePath)}-before-v{firstVersion}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");
        await using var connection = new SqliteConnection(SqliteWorkspaceDatabase.ConnectionString(databasePath));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $copy";
        command.Parameters.AddWithValue("$copy", copy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Workspace database copied to {Copy} before it is brought to version {Version}", copy, firstVersion);
    }
}
