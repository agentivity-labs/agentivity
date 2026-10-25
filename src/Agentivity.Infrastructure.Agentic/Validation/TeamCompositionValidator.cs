using Agentivity.Domain.Team;

namespace Agentivity.Infrastructure.Agentic.Validation;

/// <summary>
/// Save-time composition rules for teams with non-LLM members (nested teams, workflows).
/// <para>
/// In the handoff pattern, routing normally belongs to the active member's LLM (agent_next_step).
/// A non-LLM member cannot decide a route; the orchestrator covers it with the return-to-sender
/// rule (its output goes back to the agent that delegated to it) or a single explicit Delegate
/// connection. One configuration remains unresolvable and is rejected here, with a clear message
/// for the editor, instead of failing at run time: a non-LLM member declaring several outgoing
/// Delegate connections (nobody can choose among them).
/// </para>
/// <para>
/// There is no save-time check on which member goes first: the entry point is decided
/// dynamically at run time (see <c>HandoffGraphOrchestrator.ResolveEntryTopologyPositionAsync</c>),
/// scoped to every member including non-agents — a non-LLM member picked as entry simply runs
/// once and the chain completes (no earlier sender to return to), which is a valid outcome.
/// </para>
/// </summary>
public static class TeamCompositionValidator
{
    private const string HandoffOrchestratorId = "handoff";

    /// <summary>Returns an error message when the composition is invalid, null otherwise.</summary>
    public static string? Validate(TeamEntity team)
    {
        if (!string.Equals(team.OrchestratorId, HandoffOrchestratorId, StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var member in team.Members.Where(m => m.MemberType != TeamMemberType.Agent))
        {
            var outgoingDelegates = team.Connections.Count(c => c.FromTopologyPositionId == member.TopologyPositionId && c.ToTopologyPositionId != member.TopologyPositionId && c.Types.Contains(TeamConnectionType.Delegate));
            if (outgoingDelegates > 1)
                return $"Member '{member.DisplayName ?? member.MemberEntityId}' is a {member.MemberType.ToString().ToLowerInvariant()} with {outgoingDelegates} outgoing delegate connections — a non-agent member cannot choose between several routes. Keep at most one outgoing connection (or none: its result then returns to the agent that delegated to it).";
        }

        return null;
    }
}
