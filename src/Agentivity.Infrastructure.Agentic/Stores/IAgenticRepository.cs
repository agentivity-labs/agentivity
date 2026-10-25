using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Catalogue;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Infrastructure.Agentic;

/// <summary>
/// Unified repository for the agentic library.
/// Agents and teams share the same folder / browse space;
/// CRUD and versioning are typed per entity kind.
/// </summary>
public interface IAgenticRepository
{
    // ── Browse / Folders (shared) ──────────────────────────────────────────

    /// <summary>
    /// Browses the library tree at the given folder (null = root).
    /// Returns a <see cref="CatalogBrowseResult"/> with typed items (<c>type: "agent"</c> or <c>type: "team"</c>).
    /// </summary>
    ValueTask<CatalogBrowseResult> BrowseAsync(string? folderId = null, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<WorkspaceFolder>> ListFoldersAsync(string? parentFolderId = null, CancellationToken cancellationToken = default);

    ValueTask<StorageResult<WorkspaceFolder>> CreateFolderAsync(string name, string? parentFolderId = null, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> RenameFolderAsync(string folderId, string newName, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> DeleteFolderAsync(string folderId, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> MoveFolderAsync(string folderId, string? targetParentFolderId, CancellationToken cancellationToken = default);

    // ── Agents ────────────────────────────────────────────────────────────

    ValueTask<IReadOnlyList<AgentEntity>> ListAgentsAsync(string? folderId = null, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<AgentEntity>> GetAgentAsync(string agentId, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<AgentEntity>> SaveAgentAsync(AgentEntity entry, string? folderId = null, CancellationToken cancellationToken = default);

    ValueTask<OperationResult> DeleteAgentAsync(string agentId, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> MoveAgentAsync(string agentId, string? targetFolderId, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<AgenticVersionInfo>> GetAgentVersionAsync(string agentId, CancellationToken cancellationToken = default);

    // ── Teams ─────────────────────────────────────────────────────────────

    ValueTask<IReadOnlyList<TeamEntity>> ListTeamsAsync(string? folderId = null, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<TeamEntity>> GetTeamAsync(string teamId, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<TeamEntity>> SaveTeamAsync(TeamEntity entry, string? folderId = null, CancellationToken cancellationToken = default);

    ValueTask<OperationResult> DeleteTeamAsync(string teamId, CancellationToken cancellationToken = default);

    ValueTask<StorageResult> MoveTeamAsync(string teamId, string? targetFolderId, CancellationToken cancellationToken = default);

    ValueTask<OperationResult<AgenticVersionInfo>> GetTeamVersionAsync(string teamId, CancellationToken cancellationToken = default);
}
