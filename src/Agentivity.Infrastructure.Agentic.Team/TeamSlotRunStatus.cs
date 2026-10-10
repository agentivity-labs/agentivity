using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// Point-in-time run status for a single team member topologyPosition.
/// Mirrors <c>WorkflowNodeRunStatus</c> for the team editor's node-highlighting mechanism.
/// </summary>
public sealed record TeamTopologyPositionRunStatus(string TopologyPositionId, string State, string? Error, DateTimeOffset OccurredAtUtc);

/// <summary>
/// In-memory, per-run registry of the latest <see cref="TeamTopologyPositionRunStatus"/> for each team
/// member topologyPosition. Polled by the team status endpoint so the team editor can highlight active
/// and failed agents the same way the workflow editor highlights active/failed nodes.
/// </summary>
public interface ITeamTopologyPositionStatusRegistry
{
    void SetStatus(string runId, string topologyPositionId, string state, string? error = null);

    IReadOnlyDictionary<string, TeamTopologyPositionRunStatus> GetStatuses(string runId);

    void ClearRun(string runId);
}

public sealed class TeamTopologyPositionStatusRegistry : ITeamTopologyPositionStatusRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TeamTopologyPositionRunStatus>> _runs = new(StringComparer.Ordinal);

    public void SetStatus(string runId, string topologyPositionId, string state, string? error = null)
    {
        var topologyPositions = _runs.GetOrAdd(runId, static _ => new ConcurrentDictionary<string, TeamTopologyPositionRunStatus>(StringComparer.Ordinal));
        topologyPositions[topologyPositionId] = new TeamTopologyPositionRunStatus(topologyPositionId, state, error, DateTimeOffset.UtcNow);
    }

    public IReadOnlyDictionary<string, TeamTopologyPositionRunStatus> GetStatuses(string runId) => _runs.TryGetValue(runId, out var topologyPositions) ? topologyPositions : new Dictionary<string, TeamTopologyPositionRunStatus>(StringComparer.Ordinal);

    public void ClearRun(string runId) => _runs.TryRemove(runId, out _);
}
