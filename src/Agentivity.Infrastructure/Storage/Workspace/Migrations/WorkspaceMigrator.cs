using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Storage.Workspace.Migrations;

/// <summary>What a database engine gives the <see cref="WorkspaceMigrator"/>: the way in, and what it does around the migrations.</summary>
public sealed class WorkspaceMigrationTarget
{
    /// <summary>An open connection to the database.</summary>
    public required Func<CancellationToken, Task<DbConnection>> OpenAsync { get; init; }

    /// <summary>
    /// Run first in each transaction of the migrator: where several servers share the database (Postgres), a lock held until the
    /// transaction ends, so that one of them only makes a change.
    /// </summary>
    public Func<DbConnection, DbTransaction, CancellationToken, Task>? LockAsync { get; init; }

    /// <summary>Run once before the first migration to pass, only when there is one (a copy of the database, say).</summary>
    public Func<IReadOnlyList<WorkspaceMigration>, CancellationToken, Task>? BeforeMigratingAsync { get; init; }
}

/// <summary>
/// Brings a workspace database to the last of its migrations. The numbers it went through are kept in <c>schema_migrations</c>;
/// those it lacks are passed in order, each in a transaction with the line that marks it: a migration that fails leaves nothing,
/// and stops the ones after it. A database that went through a migration the server does not know is never touched
/// (<see cref="WorkspaceDatabaseTooNewException"/>). The same for every engine: the engine says how to open it and how to lock it.
/// </summary>
public static class WorkspaceMigrator
{
    public const string LedgerTable = "schema_migrations";

    /// <summary>Passes the migrations the database lacks; returns those it passed (none when it was up to date).</summary>
    public static async Task<IReadOnlyList<WorkspaceMigration>> MigrateAsync(WorkspaceMigrationTarget target, IReadOnlyList<WorkspaceMigration> migrations, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Check(migrations);
        var known = migrations[^1].Version;

        await using var connection = await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        var applied = await AppliedOrNoneAsync(connection, cancellationToken).ConfigureAwait(false);
        if (applied.Count > 0 && applied.Max() > known)
            throw new WorkspaceDatabaseTooNewException(applied.Max(), known);

        var pending = migrations.Where(m => !applied.Contains(m.Version)).ToList();
        if (pending.Count == 0)
            return [];

        // Before anything is written, the list of migrations included: a copy made here is the database as it was.
        if (target.BeforeMigratingAsync is { } before)
            await before(pending, cancellationToken).ConfigureAwait(false);

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (target.LockAsync is { } lockLedger)
                await lockLedger(connection, transaction, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, $"CREATE TABLE IF NOT EXISTS {LedgerTable} (version INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL)", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var passed = new List<WorkspaceMigration>();
        foreach (var migration in pending)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (target.LockAsync is { } lockIt)
                    await lockIt(connection, transaction, cancellationToken).ConfigureAwait(false);
                // Another server sharing the database may have passed it while this one waited for the lock.
                if ((await AppliedAsync(connection, transaction, cancellationToken).ConfigureAwait(false)).Contains(migration.Version))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await migration.ApplyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await MarkAsync(connection, transaction, migration, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
                throw new WorkspaceMigrationFailedException(migration, ex);
            }

            logger.LogInformation("Workspace database brought to version {Version}: {Name}", migration.Version, migration.Name);
            passed.Add(migration);
        }

        return passed;
    }

    /// <summary>The numbers of the migrations the database went through (empty for one that never went through any).</summary>
    public static async Task<HashSet<int>> AppliedAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT version FROM {LedgerTable}";
        var versions = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            versions.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
        return versions;
    }

    // A database that never went through a migration has no list of them yet.
    private static async Task<HashSet<int>> AppliedOrNoneAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return await AppliedAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            return [];
        }
    }

    private static void Check(IReadOnlyList<WorkspaceMigration> migrations)
    {
        if (migrations is null || migrations.Count == 0)
            throw new ArgumentException("A workspace database has at least its first migration.", nameof(migrations));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new ArgumentException($"The migrations are numbered 1, 2, 3… in order, without a gap: found {migrations[i].Version} at place {i + 1} ({migrations[i].Name}).", nameof(migrations));
        }
    }

    private static async Task MarkAsync(DbConnection connection, DbTransaction transaction, WorkspaceMigration migration, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {LedgerTable} (version, name, applied_at) VALUES (@version, @name, @appliedAt)";
        Add(command, "@version", migration.Version);
        Add(command, "@name", migration.Name);
        Add(command, "@appliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollBackAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // The connection is gone: the database dropped the transaction with it.
        }
    }
}
