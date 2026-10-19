using Agentivity.Domain;
using Agentivity.Infrastructure.DataTables.Domain;
using Agentivity.Infrastructure.KnowledgeBases.Domain;
using Agentivity.Infrastructure.Repository.Entities;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>
/// An asset of the workspace a step input can point at (an input of type AssetRef, such as a Data Table tool's table or a
/// Knowledge Base node's base): its kind, id and name, the folder it is in (its path, null at the top level) and, in a few
/// words, what it holds.
/// </summary>
public sealed record AssetRef(string Kind, string Id, string Name, string? Folder, string? Description);

/// <summary>What the assistant may know about the workspace's assets, so it points a step at the one the person means.</summary>
public interface IAssetLookup
{
    /// <summary>The assets, optionally of one kind (the AssetKind of the input, e.g. "datatable").</summary>
    Task<IReadOnlyList<AssetRef>> ListAsync(string? kind = null, CancellationToken cancellationToken = default);
}

/// <summary>Every kind of asset the workspace has, from the lookup of each.</summary>
public sealed class CompositeAssetLookup(IEnumerable<IAssetLookup> lookups) : IAssetLookup
{
    private readonly IReadOnlyList<IAssetLookup> _lookups = [.. lookups];

    public async Task<IReadOnlyList<AssetRef>> ListAsync(string? kind = null, CancellationToken cancellationToken = default)
    {
        var all = new List<AssetRef>();
        foreach (var lookup in _lookups)
            all.AddRange(await lookup.ListAsync(kind, cancellationToken).ConfigureAwait(false));
        return all;
    }
}

/// <summary>The Data Tables of the workspace, read from their library: each with its folder and its columns.</summary>
public sealed class DataTableAssetLookup(IEntityRepository<DataTableEntity> tables) : IAssetLookup
{
    public async Task<IReadOnlyList<AssetRef>> ListAsync(string? kind = null, CancellationToken cancellationToken = default)
    {
        if (kind is not null && !string.Equals(kind, DataTableEntity.ObjectType, StringComparison.OrdinalIgnoreCase))
            return [];
        var folders = await LibraryFolders.OfAsync(tables, cancellationToken).ConfigureAwait(false);
        var all = await tables.ListAsync(null, cancellationToken).ConfigureAwait(false);
        return [.. all.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => new AssetRef(DataTableEntity.ObjectType, t.Id, t.Name, folders.GetValueOrDefault(t.Id), Columns(t)))];
    }

    private static string? Columns(DataTableEntity table) =>
        table.Columns.Count == 0 ? null : "Columns: " + string.Join(", ", table.Columns.Select(c => $"{c.Name} ({c.Type}{(c.Required ? ", required" : "")})"));
}

/// <summary>The Knowledge Bases of the workspace, read from their library: each with its folder and what it reads.</summary>
public sealed class KnowledgeBaseAssetLookup(IEntityRepository<KnowledgeBaseEntity> bases) : IAssetLookup
{
    // The setting that says, in a word, where a base reads (a folder, a feed, a site…).
    private static readonly string[] WhereItReads = ["path", "urls", "feed_url", "sitemap_url", "url", "database_path"];

    public async Task<IReadOnlyList<AssetRef>> ListAsync(string? kind = null, CancellationToken cancellationToken = default)
    {
        if (kind is not null && !string.Equals(kind, KnowledgeBaseEntity.ObjectType, StringComparison.OrdinalIgnoreCase))
            return [];
        var folders = await LibraryFolders.OfAsync(bases, cancellationToken).ConfigureAwait(false);
        var all = await bases.ListAsync(null, cancellationToken).ConfigureAwait(false);
        return [.. all.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Select(b => new AssetRef(KnowledgeBaseEntity.ObjectType, b.Id, b.Name, folders.GetValueOrDefault(b.Id), Reads(b)))];
    }

    private static string Reads(KnowledgeBaseEntity knowledgeBase)
    {
        var where = WhereItReads.Select(name => knowledgeBase.Settings[name]?.ToString()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return where is null ? $"Reads: {knowledgeBase.SourceType}" : $"Reads: {knowledgeBase.SourceType}, {where}";
    }
}

/// <summary>Where each item of a library is: the path of its folder, from the top level down; items at the top level are not listed.</summary>
internal static class LibraryFolders
{
    public static async Task<Dictionary<string, string>> OfAsync<TEntity>(IEntityRepository<TEntity> library, CancellationToken cancellationToken)
        where TEntity : Entity
    {
        var folderOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string? Id, string? Path)>([(null, null)]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var folder in await library.ListFoldersAsync(parent.Id, cancellationToken).ConfigureAwait(false))
            {
                if (!seen.Add(folder.FolderId) || !string.Equals(folder.ParentFolderId ?? "", parent.Id ?? "", StringComparison.OrdinalIgnoreCase))
                    continue;
                var path = parent.Path is null ? folder.Name : $"{parent.Path}/{folder.Name}";
                foreach (var item in await library.ListAsync(folder.FolderId, cancellationToken).ConfigureAwait(false))
                    folderOf[item.Id] = path;
                pending.Enqueue((folder.FolderId, path));
            }
        }

        return folderOf;
    }
}
