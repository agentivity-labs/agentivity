using Agentivity.Infrastructure.Identity;

namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// What the stores of the domains kept by space (the libraries) add to their SQL, so that nothing outside the spaces of the work
/// being done now is read or written (<see cref="SpaceScope"/>). The same for every engine: only the parameter prefix differs
/// (<c>$</c> for SQLite, <c>@</c> for Postgres).
/// </summary>
public static class WorkspaceSpaceFilter
{
    /// <summary>
    /// What may be read, changed or deleted: the spaces seen (nothing more to add when they are all seen). Appended to a WHERE.
    /// </summary>
    public static (string Sql, (string Name, object? Value)[] Parameters) Seen(char prefix, string column = "space_id")
    {
        var access = SpaceScope.Access;
        if (access.AllSpaces)
            return (string.Empty, []);
        if (access.Visible.Count == 0)
            return (" AND 1 = 0", []);

        var names = access.Visible.Select((_, i) => $"{prefix}seen{i}").ToList();
        return ($" AND {column} IN ({string.Join(", ", names)})", [.. access.Visible.Select((space, i) => (names[i], (object?)space))]);
    }

    /// <summary>What a list shows: what is seen, and only the space being looked at when one is chosen.</summary>
    public static (string Sql, (string Name, object? Value)[] Parameters) Shown(char prefix, string column = "space_id")
    {
        var (sql, parameters) = Seen(prefix, column);
        return SpaceScope.ViewSpaceId is { } view ? ($"{sql} AND {column} = {prefix}view", [.. parameters, ($"{prefix}view", view)]) : (sql, parameters);
    }
}
