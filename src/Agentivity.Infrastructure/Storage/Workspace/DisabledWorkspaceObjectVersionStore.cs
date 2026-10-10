namespace Agentivity.Infrastructure.Storage.Workspace;

public sealed class DisabledWorkspaceObjectVersionStore : IWorkspaceObjectVersionStore
{
    public WorkspaceObjectVersioningCapabilities VersioningCapabilities => WorkspaceObjectVersioningCapabilities.Disabled;

    public ValueTask<StorageResult<WorkspaceObjectVersionInfo>> CreateVersionAsync(string objectType, string objectId, WorkspaceObjectVersionCreateRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult<WorkspaceObjectVersionInfo>.Fail("Workspace object versioning is disabled."));

    public ValueTask<IReadOnlyList<WorkspaceObjectVersionInfo>> ListVersionsAsync(string objectType, string objectId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<WorkspaceObjectVersionInfo>>(Array.Empty<WorkspaceObjectVersionInfo>());

    public ValueTask<StorageResult<WorkspaceObjectEnvelope>> GetVersionAsync(string objectType, string objectId, string versionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(StorageResult<WorkspaceObjectEnvelope>.Fail("Workspace object versioning is disabled."));

    public ValueTask<StorageResult<WorkspaceObjectVersionInfo>> RestoreVersionAsync(string objectType, string objectId, string versionId, WorkspaceObjectVersionRestoreRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(StorageResult<WorkspaceObjectVersionInfo>.Fail("Workspace object versioning is disabled."));
}
