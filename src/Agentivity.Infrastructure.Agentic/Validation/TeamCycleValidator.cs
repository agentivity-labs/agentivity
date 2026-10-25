using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Agentic.Team;

namespace Agentivity.Infrastructure.Agentic.Validation;

/// <summary>
/// Detects reference cycles in nested <see cref="TeamEntity"/> members before persistence.
/// Uses an iterative DFS on the team graph exposed by <see cref="IAgenticRepository"/>.
/// Cycles involving <see cref="TeamMemberType.Agent"/> members are impossible by definition
/// (agents are leaves); only <see cref="TeamMemberType.Team"/> topologyPositions can form cycles.
/// </summary>
public sealed class TeamCycleValidator
{
    private const int MaxDepth = 10;

    private readonly IAgenticRepository _repo;

    public TeamCycleValidator(IAgenticRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    public async ValueTask<OperationResult> ValidateAsync(TeamEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return await DfsAsync(entity, visited, ancestors, depth: 0, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<OperationResult> DfsAsync(TeamEntity node, HashSet<string> visited, HashSet<string> ancestors, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
            return OperationResult.Failure($"Maximum team nesting depth ({MaxDepth}) exceeded. Check for indirect cycles or reduce nesting.");

        ancestors.Add(node.Id);

        foreach (var topologyPosition in node.Members)
        {
            if (topologyPosition.MemberType is not TeamMemberType.Team)
                continue;

            var memberId = topologyPosition.MemberEntityId;

            if (ancestors.Contains(memberId))
                return OperationResult.Failure($"Team cycle detected: '{node.Id}' references '{memberId}' which is already an ancestor in the membership chain.");

            if (visited.Contains(memberId))
                continue;

            var childResult = await _repo.GetTeamAsync(memberId, ct).ConfigureAwait(false);
            if (!childResult.IsSuccess)
                continue;

            var childValidation = await DfsAsync(childResult.Value!, visited, ancestors, depth + 1, ct).ConfigureAwait(false);

            if (!childValidation.IsSuccess)
                return childValidation;
        }

        ancestors.Remove(node.Id);
        visited.Add(node.Id);
        return OperationResult.Success();
    }
}
