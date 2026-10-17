using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Agentic.Validation;
using Agentivity.Infrastructure.Catalogue;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Storage;
using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Infrastructure.Agentic;

/// <summary>
/// <see cref="IAgenticRepository"/> implementation — a thin façade over the two DI-registered
/// <see cref="IEntityRepository{TEntity}"/> instances for <see cref="AgentEntity"/> and
/// <see cref="TeamEntity"/> (sharing one workspace root). Everything generic (CRUD, folders,
/// browse, version info) is delegated; this class keeps only what is genuinely specific to
/// Agentic: team cycle/composition validation and cross-referencing member display names
/// between agents and teams.
/// </summary>
public sealed class WorkspaceAgenticRepository : IAgenticRepository
{
    private readonly IEntityRepository<AgentEntity> _agents;
    private readonly IEntityRepository<TeamEntity> _teams;
    private readonly TeamCycleValidator _cycleValidator;

    public WorkspaceAgenticRepository(IEntityRepository<AgentEntity> agents, IEntityRepository<TeamEntity> teams)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(teams);

        _agents = agents;
        _teams = teams;
        _cycleValidator = new TeamCycleValidator(this);
    }

    // ── Browse / Folders ─────────────────────────────────────────────────

    public async ValueTask<CatalogBrowseResult> BrowseAsync(string? folderId = null, CancellationToken cancellationToken = default)
    {
        // Agents and teams share one folder tree — combine both sub-repos' items under the
        // same folder listing (which is identical from either, since folders aren't scoped by
        // object type — see WorkspaceEntityRepository).
        var agentBrowse = await _agents.BrowseAsync(folderId, cancellationToken).ConfigureAwait(false);
        var teamBrowse = await _teams.BrowseAsync(folderId, cancellationToken).ConfigureAwait(false);

        var items = new List<CatalogBrowseItem>(agentBrowse.Items.Count + teamBrowse.Items.Count);
        items.AddRange(agentBrowse.Items);
        items.AddRange(teamBrowse.Items);

        return new CatalogBrowseResult(agentBrowse.Current, agentBrowse.Folders, items);
    }

    public ValueTask<IReadOnlyList<WorkspaceFolder>> ListFoldersAsync(string? parentFolderId = null, CancellationToken cancellationToken = default) => _agents.ListFoldersAsync(parentFolderId, cancellationToken);

    public ValueTask<StorageResult<WorkspaceFolder>> CreateFolderAsync(string name, string? parentFolderId = null, CancellationToken cancellationToken = default) => _agents.CreateFolderAsync(name, parentFolderId, cancellationToken);

    public ValueTask<StorageResult> RenameFolderAsync(string folderId, string newName, CancellationToken cancellationToken = default) => _agents.RenameFolderAsync(folderId, newName, cancellationToken);

    public ValueTask<StorageResult> DeleteFolderAsync(string folderId, CancellationToken cancellationToken = default) => _agents.DeleteFolderAsync(folderId, cancellationToken);

    public ValueTask<StorageResult> MoveFolderAsync(string folderId, string? targetParentFolderId, CancellationToken cancellationToken = default) => _agents.MoveFolderAsync(folderId, targetParentFolderId, cancellationToken);

    // ── Agents ────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<AgentEntity>> ListAgentsAsync(string? folderId = null, CancellationToken cancellationToken = default) => _agents.ListAsync(folderId, cancellationToken);

    public ValueTask<OperationResult<AgentEntity>> GetAgentAsync(string agentId, CancellationToken cancellationToken = default) => _agents.GetAsync(agentId, cancellationToken);

    public ValueTask<OperationResult<AgentEntity>> SaveAgentAsync(AgentEntity entry, string? folderId = null, CancellationToken cancellationToken = default) => _agents.SaveAsync(entry, folderId, cancellationToken);

    public ValueTask<OperationResult> DeleteAgentAsync(string agentId, CancellationToken cancellationToken = default) => _agents.DeleteAsync(agentId, cancellationToken);

    public ValueTask<StorageResult> MoveAgentAsync(string agentId, string? targetFolderId, CancellationToken cancellationToken = default) => _agents.MoveAsync(agentId, targetFolderId, cancellationToken);

    public async ValueTask<OperationResult<AgenticVersionInfo>> GetAgentVersionAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var result = await _agents.GetVersionAsync(agentId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is not null ? OperationResult<AgenticVersionInfo>.Success(new AgenticVersionInfo(result.Value.Id, result.Value.Name, result.Value.UpdatedAt, "agent")) : OperationResult<AgenticVersionInfo>.Failure(result.Error ?? $"Agent '{agentId}' not found.");
    }

    // ── Teams ─────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<TeamEntity>> ListTeamsAsync(string? folderId = null, CancellationToken cancellationToken = default) => _teams.ListAsync(folderId, cancellationToken);

    public ValueTask<OperationResult<TeamEntity>> GetTeamAsync(string teamId, CancellationToken cancellationToken = default) => _teams.GetAsync(teamId, cancellationToken);

    public async ValueTask<OperationResult<TeamEntity>> SaveTeamAsync(TeamEntity entry, string? folderId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var cycleCheck = await _cycleValidator.ValidateAsync(entry, cancellationToken).ConfigureAwait(false);
        if (!cycleCheck.IsSuccess)
            return OperationResult<TeamEntity>.Failure(cycleCheck.Error!);

        if (Validation.TeamCompositionValidator.Validate(entry) is { } compositionError)
            return OperationResult<TeamEntity>.Failure(compositionError);

        if (Validation.TeamMemberPresentation.Validate(entry) is { } presentationError)
            return OperationResult<TeamEntity>.Failure(presentationError);

        // Members is not `init` on TeamEntity (set only via its constructor) — rebuild the
        // instance to inject resolved display names, exactly as before this repository was
        // generalized. CreatedAt/UpdatedAt are left for WorkspaceEntityRepository's own touch.
        var membersWithNames = await ResolveMemberInfoAsync(entry.Members, cancellationToken).ConfigureAwait(false);
        var prepared = new TeamEntity(entry.Id, entry.Name, entry.OrchestratorId, membersWithNames, entry.Connections)
        {
            Goal = entry.Goal,
            Role = entry.Role,
            Annotations = entry.Annotations,
            // Tags is a shared Entity base field — carried through by the same convention
            // used everywhere else an entity gets rebuilt via `new XEntity(...) { ... }`
            // instead of a record `with` (see DataTablesEndpoints.cs, WorkflowsEndpoints.cs,
            // AgenticMapper.ToAgent/ToTeam): every such reconstruction site must re-list every
            // base field it wants to keep, and this one had silently dropped Tags.
            Tags = entry.Tags,
            GroupColors = Validation.TeamMemberPresentation.NormalizeGroupColors(entry),
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
        };

        return await _teams.SaveAsync(prepared, folderId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<OperationResult> DeleteTeamAsync(string teamId, CancellationToken cancellationToken = default) => _teams.DeleteAsync(teamId, cancellationToken);

    public ValueTask<StorageResult> MoveTeamAsync(string teamId, string? targetFolderId, CancellationToken cancellationToken = default) => _teams.MoveAsync(teamId, targetFolderId, cancellationToken);

    public async ValueTask<OperationResult<AgenticVersionInfo>> GetTeamVersionAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var result = await _teams.GetVersionAsync(teamId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is not null ? OperationResult<AgenticVersionInfo>.Success(new AgenticVersionInfo(result.Value.Id, result.Value.Name, result.Value.UpdatedAt, "team")) : OperationResult<AgenticVersionInfo>.Failure(result.Error ?? $"Team '{teamId}' not found.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async ValueTask<IReadOnlyList<TeamMemberTopologyPosition>> ResolveMemberInfoAsync(IReadOnlyList<TeamMemberTopologyPosition> members, CancellationToken cancellationToken)
    {
        var resolved = new List<TeamMemberTopologyPosition>(members.Count);
        foreach (var member in members)
        {
            var displayName = member.DisplayName;
            var description = member.Role;

            // Only hit the store when something is actually missing — preserves any
            // user-customized DisplayName, and avoids redundant lookups on re-save.
            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(description))
            {
                var (resolvedName, resolvedDescription) = member.MemberType switch
                {
                    TeamMemberType.Agent => await ResolveAgentInfoAsync(member.MemberEntityId, cancellationToken).ConfigureAwait(false),
                    TeamMemberType.Team => await ResolveTeamInfoAsync(member.MemberEntityId, cancellationToken).ConfigureAwait(false),
                    _ => (null, null),
                };

                displayName = string.IsNullOrWhiteSpace(displayName) ? resolvedName : displayName;
                description = string.IsNullOrWhiteSpace(description) ? resolvedDescription : description;
            }

            // Icon and group are the user's to define: only tidied here.
            var icon = Validation.TeamMemberPresentation.NormalizeIcon(member.Icon);
            resolved.Add(member with { DisplayName = displayName, Role = description, Icon = icon, Group = Validation.TeamMemberPresentation.NormalizeGroup(member.Group) });
        }
        return resolved;
    }

    private async ValueTask<(string? Name, string? Description)> ResolveAgentInfoAsync(string agentId, CancellationToken cancellationToken)
    {
        var result = await _agents.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is not null ? (result.Value.Name, result.Value.Role) : (null, null);
    }

    private async ValueTask<(string? Name, string? Description)> ResolveTeamInfoAsync(string teamId, CancellationToken cancellationToken)
    {
        var result = await _teams.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.Value is not null ? (result.Value.Name, result.Value.Role) : (null, null);
    }
}
