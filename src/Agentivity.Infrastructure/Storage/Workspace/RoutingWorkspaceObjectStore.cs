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

    public async ValueTask<StorageResult> SaveAsync(WorkspaceObjectEnvelope envelope, CancellationToken cancellationToken = default)
    {
        // One links only what one sees: an element saved by someone who does not see every space names no element of a space they
        // do not see (a step that runs an agent of HR, a Data Table of Finance…).
        if (Identity.SpaceScope.IsLimited && await NotSeenNamedByAsync(envelope, cancellationToken).ConfigureAwait(false) is { } hidden)
            return StorageResult.Fail($"This names something you cannot see ({hidden}): one links only what is in a space one is in.");
        return await Resolve(envelope.ObjectType).SaveAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<StorageResult> MoveToSpaceAsync(string objectType, string objectId, string spaceId, CancellationToken cancellationToken = default) => Resolve(objectType).MoveToSpaceAsync(objectType, objectId, spaceId, cancellationToken);

    public ValueTask<IReadOnlyList<WorkspaceSpaceFolder>> TopFoldersAsync(string objectType, CancellationToken cancellationToken = default) => Resolve(objectType).TopFoldersAsync(objectType, cancellationToken);

    public ValueTask<StorageResult> MoveFolderToSpaceAsync(string objectType, string folderId, string spaceId, CancellationToken cancellationToken = default) => Resolve(objectType).MoveFolderToSpaceAsync(objectType, folderId, spaceId, cancellationToken);

    public bool Keeps(string objectType) => _byObjectType.ContainsKey(objectType);

    private static readonly System.Text.RegularExpressions.Regex IdShape = new("^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The first element of the libraries the payload names (by its id) that is in a space the work being done now does not see.
    private async Task<string?> NotSeenNamedByAsync(WorkspaceObjectEnvelope envelope, CancellationToken cancellationToken)
    {
        var access = Identity.SpaceScope.Access;
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Collect(System.Text.Json.JsonDocument.Parse(envelope.Payload).RootElement, named);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        named.Remove(envelope.ObjectId);
        using (Identity.SpaceScope.Unlimited())
        {
            foreach (var id in named.Take(200))
            {
                foreach (var type in WorkspaceSpaces.LibraryTypes.Values.Where(_byObjectType.ContainsKey))
                {
                    var found = await Resolve(type).GetAsync(type, id, cancellationToken).ConfigureAwait(false);
                    if (found.Success && found.Value?.Metadata.SpaceId is { } space && !access.Sees(space))
                        return $"{type} {id}";
                }
            }
        }

        return null;
    }

    private static void Collect(System.Text.Json.JsonElement element, HashSet<string> named)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value, named);
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, named);
                break;
            case System.Text.Json.JsonValueKind.String when element.GetString() is { } text:
                if (IdShape.IsMatch(text.Trim()))
                    named.Add(text.Trim());
                else if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
                {
                    // An entity is often kept as JSON in a string of its wrapper.
                    try
                    {
                        Collect(System.Text.Json.JsonDocument.Parse(text).RootElement, named);
                    }
                    catch (System.Text.Json.JsonException) { }
                }

                break;
        }
    }

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
