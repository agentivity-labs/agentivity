namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The single <see cref="IWorkspaceObjectStore"/> registered app-wide. Dispatches every call to
/// the underlying store contributed for that object type (see <see cref="IWorkspaceStoreContribution"/>) —
/// callers (endpoints, repositories) never need to know which domain, or which root path, backs
/// a given object type. A single workspace root can legitimately back several object types
/// (e.g. "agent" and "team" both live in the Agentic workspace) — that is a normal, expected
/// many-to-one mapping in the routing table, not an error.
/// </summary>
public sealed class RoutingWorkspaceObjectStore : IWorkspaceObjectStore
{
    private readonly IReadOnlyDictionary<string, IWorkspaceObjectStore> _byObjectType;

    public RoutingWorkspaceObjectStore(IEnumerable<IWorkspaceStoreContribution> contributions)
    {
        var map = new Dictionary<string, IWorkspaceObjectStore>(StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in contributions)
        {
            foreach (var objectType in contribution.ObjectTypes)
            {
                if (!map.TryAdd(objectType, contribution.Store))
                {
                    throw new InvalidOperationException($"Workspace object type '{objectType}' is contributed by more than one store — each object type must map to exactly one store instance.");
                }
            }
        }

        _byObjectType = map;
    }

    private IWorkspaceObjectStore Resolve(string objectType) =>
        _byObjectType.TryGetValue(objectType, out var store)
            ? store
            : throw new InvalidOperationException($"No workspace store registered for object type '{objectType}'. Ensure the owning module contributes it via IWorkspaceStoreContribution.");

    public ValueTask<StorageResult<WorkspaceObjectEnvelope>> GetAsync(string objectType, string objectId, CancellationToken cancellationToken = default) => Resolve(objectType).GetAsync(objectType, objectId, cancellationToken);

    public ValueTask<IReadOnlyList<WorkspaceObjectEnvelope>> ListAsync(string objectType, WorkspaceObjectQuery? query = null, CancellationToken cancellationToken = default) => Resolve(objectType).ListAsync(objectType, query, cancellationToken);

    public ValueTask<StorageResult> SaveAsync(WorkspaceObjectEnvelope envelope, CancellationToken cancellationToken = default) => Resolve(envelope.ObjectType).SaveAsync(envelope, cancellationToken);

    public ValueTask<StorageResult> DeleteAsync(string objectType, string objectId, CancellationToken cancellationToken = default) => Resolve(objectType).DeleteAsync(objectType, objectId, cancellationToken);

    public ValueTask<bool> ExistsAsync(string objectType, string objectId, CancellationToken cancellationToken = default) => Resolve(objectType).ExistsAsync(objectType, objectId, cancellationToken);

    // ── Folders ───────────────────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<WorkspaceFolder>> ListFoldersAsync(string objectType, string? parentFolderId = null, CancellationToken cancellationToken = default) => Resolve(objectType).ListFoldersAsync(objectType, parentFolderId, cancellationToken);

    public ValueTask<StorageResult<WorkspaceFolder>> CreateFolderAsync(string objectType, string name, string? parentFolderId = null, CancellationToken cancellationToken = default) => Resolve(objectType).CreateFolderAsync(objectType, name, parentFolderId, cancellationToken);

    public ValueTask<StorageResult> RenameFolderAsync(string objectType, string folderId, string newName, CancellationToken cancellationToken = default) => Resolve(objectType).RenameFolderAsync(objectType, folderId, newName, cancellationToken);

    public ValueTask<StorageResult> DeleteFolderAsync(string objectType, string folderId, CancellationToken cancellationToken = default) => Resolve(objectType).DeleteFolderAsync(objectType, folderId, cancellationToken);

    public ValueTask<StorageResult> MoveFolderAsync(string objectType, string folderId, string? targetParentFolderId, CancellationToken cancellationToken = default) => Resolve(objectType).MoveFolderAsync(objectType, folderId, targetParentFolderId, cancellationToken);

    public ValueTask<StorageResult> MoveObjectAsync(string objectType, string objectId, string? targetFolderId, CancellationToken cancellationToken = default) => Resolve(objectType).MoveObjectAsync(objectType, objectId, targetFolderId, cancellationToken);

    public ValueTask<WorkspaceBrowseResult> BrowseAsync(string objectType, string? folderId = null, CancellationToken cancellationToken = default) => Resolve(objectType).BrowseAsync(objectType, folderId, cancellationToken);
}
