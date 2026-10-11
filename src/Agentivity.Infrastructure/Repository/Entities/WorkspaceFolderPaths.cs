using Agentivity.Domain;

namespace Agentivity.Infrastructure.Repository.Entities;

/// <summary>
/// One place for things that live in several libraries. Each library (agents and teams, workflows, data tables) keeps its own
/// folders, so a place common to all of them is a path: the names of the folders from the top level down. It is followed in
/// each library that receives something, and the folders the library lacks are made, so that what belongs together sits under
/// the same name everywhere.
/// </summary>
public static class WorkspaceFolderPaths
{
    /// <summary>
    /// The folder at <paramref name="path"/> in <paramref name="library"/>, made where it does not exist yet. Null for the top
    /// level (an empty path). Names are matched whatever their case, so "Finance" is not made beside "finance".
    /// </summary>
    /// <exception cref="InvalidOperationException">A folder of the path could not be made.</exception>
    public static async Task<string?> FindOrCreateAsync<TEntity>(IEntityRepository<TEntity> library, IReadOnlyList<string>? path, CancellationToken cancellationToken = default)
        where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(library);

        string? parent = null;
        foreach (var name in (path ?? []).Select(p => p?.Trim() ?? string.Empty).Where(p => p.Length > 0))
        {
            var here = await library.ListFoldersAsync(parent, cancellationToken).ConfigureAwait(false);
            var found = here.FirstOrDefault(f => string.Equals(f.Name.Trim(), name, StringComparison.OrdinalIgnoreCase) && string.Equals(f.ParentFolderId ?? string.Empty, parent ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                var made = await library.CreateFolderAsync(name, parent, cancellationToken).ConfigureAwait(false);
                if (!made.Success || made.Value is null)
                {
                    throw new InvalidOperationException(made.Error ?? $"'{name}' could not be created.");
                }

                found = made.Value;
            }

            parent = found.FolderId;
        }

        return parent;
    }
}
