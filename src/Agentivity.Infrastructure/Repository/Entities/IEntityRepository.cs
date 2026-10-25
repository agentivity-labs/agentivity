using Agentivity.Domain;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Catalogue;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Infrastructure.Repository.Entities;

/// <summary>
/// Generic entity repository — CRUD + folders + browse + lightweight version info, backed
/// exclusively by <see cref="IWorkspaceObjectStore"/>/<see cref="IWorkspaceObjectVersionStore"/>
/// (the app-wide routing store). Agent, team, workflow and prompt are all instantiations of
/// <see cref="WorkspaceEntityRepository{TEntity}"/> rather than separate hand-rolled implementations.
/// </summary>
public interface IEntityRepository<TEntity> where TEntity : Entity
{
    ValueTask<IReadOnlyList<TEntity>> ListAsync(string? folderId = null, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<TEntity>> GetAsync(string id, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<TEntity>> SaveAsync(TEntity entity, string? folderId = null, CancellationToken cancellationToken = default);

    ValueTask<OperationResult> DeleteAsync(string id, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> MoveAsync(string id, string? targetFolderId, CancellationToken cancellationToken = default);

    ValueTask<CatalogBrowseResult> BrowseAsync(string? folderId = null, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkspaceFolder>> ListFoldersAsync(string? parentFolderId = null, CancellationToken cancellationToken = default);

    ValueTask<StorageResult<WorkspaceFolder>> CreateFolderAsync(string name, string? parentFolderId = null, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> RenameFolderAsync(string folderId, string newName, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> DeleteFolderAsync(string folderId, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> MoveFolderAsync(string folderId, string? targetParentFolderId, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<EntityVersionInfo>> GetVersionAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Lightweight version info for the current state of an entity (not the full version history — see <see cref="IWorkspaceObjectVersionStore"/> for that).</summary>
public sealed record EntityVersionInfo(string Id, string Name, DateTimeOffset? UpdatedAt);
