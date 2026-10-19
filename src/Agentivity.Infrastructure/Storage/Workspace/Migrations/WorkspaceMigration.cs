using System.Data.Common;

namespace Agentivity.Infrastructure.Storage.Workspace.Migrations;

/// <summary>
/// One change of the workspace database, numbered: the database keeps the numbers it went through (<c>schema_migrations</c>), and
/// a server passes, in order, those it does not have. A migration runs in the transaction that marks it, so it is all done or not
/// at all. <see cref="Version"/> 1 is the database as it was made before migrations existed: it must work on a database that
/// already has those tables (it only makes what is missing).
/// </summary>
/// <param name="Version">Its number: unique, never reused, never changed once released.</param>
/// <param name="Name">What it does, in a few words (written in the database and in the logs).</param>
/// <param name="ApplyAsync">The change, made with the connection and transaction it is given.</param>
public sealed record WorkspaceMigration(int Version, string Name, Func<DbConnection, DbTransaction, CancellationToken, Task> ApplyAsync)
{
    /// <summary>A migration that is one SQL script.</summary>
    public static WorkspaceMigration Sql(int version, string name, string sql) =>
        new(version, name, async (connection, transaction, cancellationToken) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        });
}

/// <summary>The database went through a migration this server does not know: it comes from a newer version of Agentivity.</summary>
public sealed class WorkspaceDatabaseTooNewException(int databaseVersion, int serverVersion)
    : InvalidOperationException($"The workspace database is at version {databaseVersion}, made by a newer version of Agentivity; this server knows versions up to {serverVersion}. Start the newer version, or restore a copy of the database made for this one. Nothing was changed.")
{
    public int DatabaseVersion { get; } = databaseVersion;
    public int ServerVersion { get; } = serverVersion;
}

/// <summary>A migration failed: nothing of it was kept, and the ones after it were not run.</summary>
public sealed class WorkspaceMigrationFailedException(WorkspaceMigration migration, Exception inner)
    : InvalidOperationException($"The workspace database could not be brought to version {migration.Version} ({migration.Name}): {inner.Message} Nothing of this change was kept; the database is still at the version before it.", inner)
{
    public int Version { get; } = migration.Version;
}
