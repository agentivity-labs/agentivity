namespace Agentivity.Infrastructure.Storage.Workspace;

/// <summary>
/// The single <see cref="IWorkspaceObjectVersionStore"/> registered app-wide. Dispatches by
/// object type to the version store contributed alongside the matching
/// <see cref="IWorkspaceStoreContribution"/> — mirrors <see cref="RoutingWorkspaceObjectStore"/>.
/// A contribution with no version store (the loaded storage module provides none, e.g. Basic — versioning is
/// not part of it) simply has no entry here — calls for its object types report versioning as
/// unavailable rather than throwing, since "no versioning in the Basic edition" is expected, not a bug.
/// </summary>
public sealed class RoutingWorkspaceObjectVersionStore : IWorkspaceObjectVersionStore
{
    private readonly IReadOnlyDictionary<string, IWorkspaceObjectVersionStore> _byObjectType;
    private readonly WorkspaceObjectVersioningCapabilities _capabilities;

    public RoutingWorkspaceObjectVersionStore(IEnumerable<IWorkspaceStoreContribution> contributions)
    {
        var map = new Dictionary<string, IWorkspaceObjectVersionStore>(StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in contributions)
        {
            if (contribution.VersionStore is null)
            {
                continue;
            }

            foreach (var objectType in contribution.ObjectTypes)
            {
                if (!map.TryAdd(objectType, contribution.VersionStore))
                {
                    throw new InvalidOperationException($"Workspace object type '{objectType}' is contributed by more than one version store — each object type must map to exactly one store instance.");
                }
            }
        }

        _byObjectType = map;
        // All version stores are built through the same IWorkspaceProvisioner (one loaded
        // storage module), so capabilities are identical across contributions.
        _capabilities = map.Values.FirstOrDefault()?.VersioningCapabilities ?? WorkspaceObjectVersioningCapabilities.Disabled;
    }

    public WorkspaceObjectVersioningCapabilities VersioningCapabilities => _capabilities;

    private IWorkspaceObjectVersionStore? Resolve(string objectType) => _byObjectType.GetValueOrDefault(objectType);

    public ValueTask<StorageResult<WorkspaceObjectVersionInfo>> CreateVersionAsync(string objectType, string objectId, WorkspaceObjectVersionCreateRequest request, CancellationToken cancellationToken = default) =>
        Resolve(objectType) is { } store
            ? store.CreateVersionAsync(objectType, objectId, request, cancellationToken)
            : ValueTask.FromResult(StorageResult<WorkspaceObjectVersionInfo>.Fail($"Versioning is not available for object type '{objectType}'."));

    public ValueTask<IReadOnlyList<WorkspaceObjectVersionInfo>> ListVersionsAsync(string objectType, string objectId, CancellationToken cancellationToken = default) =>
        Resolve(objectType) is { } store
            ? store.ListVersionsAsync(objectType, objectId, cancellationToken)
            : ValueTask.FromResult<IReadOnlyList<WorkspaceObjectVersionInfo>>([]);

    public ValueTask<StorageResult<WorkspaceObjectEnvelope>> GetVersionAsync(string objectType, string objectId, string versionId, CancellationToken cancellationToken = default) =>
        Resolve(objectType) is { } store
            ? store.GetVersionAsync(objectType, objectId, versionId, cancellationToken)
            : ValueTask.FromResult(StorageResult<WorkspaceObjectEnvelope>.Fail($"Versioning is not available for object type '{objectType}'."));

    public ValueTask<StorageResult<WorkspaceObjectVersionInfo>> RestoreVersionAsync(string objectType, string objectId, string versionId, WorkspaceObjectVersionRestoreRequest request, CancellationToken cancellationToken = default) =>
        Resolve(objectType) is { } store
            ? store.RestoreVersionAsync(objectType, objectId, versionId, request, cancellationToken)
            : ValueTask.FromResult(StorageResult<WorkspaceObjectVersionInfo>.Fail($"Versioning is not available for object type '{objectType}'."));
}
