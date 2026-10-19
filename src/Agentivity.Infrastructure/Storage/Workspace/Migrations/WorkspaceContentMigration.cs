using System.Data.Common;
using System.Text;

namespace Agentivity.Infrastructure.Storage.Workspace.Migrations;

/// <summary>
/// A column of an engine's tables that holds what an object of the workspace holds (its JSON), with the columns that name the
/// row: the objects themselves, and, where the engine keeps them, the versions of the objects.
/// </summary>
public sealed record WorkspaceContentColumn(string Table, string Column, IReadOnlyList<string> Key);

/// <summary>
/// A migration of what the objects hold rather than of the tables: one rewrite, the same for every engine, applied to every
/// column the engine names (<see cref="WorkspaceContentColumn"/>). Only the rows that hold one of <c>mustContain</c> are read; a
/// rewrite that changes nothing leaves the row as it is (its dates included).
/// </summary>
public static class WorkspaceContentMigration
{
    /// <param name="rewrite">The new content, or null to leave it as it is.</param>
    public static WorkspaceMigration Rewrite(int version, string name, IReadOnlyList<WorkspaceContentColumn> columns, IReadOnlyList<string> mustContain, Func<string, string?> rewrite)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rewrite);
        if (mustContain is null || mustContain.Count == 0)
            throw new ArgumentException("A content migration says which texts the rows it changes hold.", nameof(mustContain));

        return new WorkspaceMigration(version, name, async (connection, transaction, cancellationToken) =>
        {
            foreach (var column in columns)
                await RewriteAsync(connection, transaction, column, mustContain, rewrite, cancellationToken).ConfigureAwait(false);
        });
    }

    private static async Task RewriteAsync(DbConnection connection, DbTransaction transaction, WorkspaceContentColumn column, IReadOnlyList<string> mustContain, Func<string, string?> rewrite, CancellationToken cancellationToken)
    {
        // Read first, then write: a reader open on the connection would hold the rows being changed.
        var changed = new List<(object?[] Key, string Content)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            var filter = string.Join(" OR ", mustContain.Select((_, i) => $"{column.Column} LIKE @like{i} ESCAPE '\\'"));
            select.CommandText = $"SELECT {string.Join(", ", column.Key)}, {column.Column} FROM {column.Table} WHERE {filter}";
            for (var i = 0; i < mustContain.Count; i++)
                Add(select, $"@like{i}", "%" + EscapeLike(mustContain[i]) + "%");

            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var content = reader.GetString(column.Key.Count);
                if (rewrite(content) is { } rewritten && !string.Equals(rewritten, content, StringComparison.Ordinal))
                {
                    var key = new object?[column.Key.Count];
                    for (var k = 0; k < key.Length; k++)
                        key[k] = reader.GetValue(k);
                    changed.Add((key, rewritten));
                }
            }
        }

        foreach (var (key, content) in changed)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {column.Table} SET {column.Column} = @content WHERE {string.Join(" AND ", column.Key.Select((k, i) => $"{k} = @key{i}"))}";
            Add(update, "@content", content);
            for (var k = 0; k < key.Length; k++)
                Add(update, $"@key{k}", key[k]);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string EscapeLike(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '%' or '_' or '\\')
                escaped.Append('\\');
            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
