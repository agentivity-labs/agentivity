using Agentivity.Infrastructure.Graph;

namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// Graph task metadata for team orchestrators.
/// Stores the topologyPosition identity so the task can be reconstructed from the graph restore point
/// on resume without replaying OnTaskCompletedAsync.
/// <para>
/// The parent→child run edge is NOT stored here: the run tree lives in the durable RunRecords
/// (single source of truth). A restored task re-finds its child run by querying the run registry
/// for the child whose StepId matches the task id.
/// </para>
/// </summary>
/// <param name="AskedByTopologyPositionId">
/// Set only when this task is answering a pending <c>ask_agent</c> question — the topology
/// position of whoever asked it. Lets <c>AbstractTeamGraphOrchestrator.NotifyTasksRestored</c>
/// replay the pending-question return-address stack by walking restored tasks in order (push on
/// ask, pop on answer) without any separate persisted state — this rides the same durable
/// <c>TeamProgress</c>/<c>GraphRestorePoint</c> JSON round-trip every other task field already
/// uses, surviving both a HIL pause and a full process restart.
/// </param>
/// <param name="IsRespondAgentResume">
/// True only for the dispatch that resumes the ORIGINAL ASKER with the answer, after the position
/// holding the pending question called the explicit <c>respond_agent</c> action. Distinct from an
/// ordinary re-dispatch of the same asker for an unrelated reason (allowed at any time while a
/// question is pending — respond_agent is the only thing that resolves it) — restore-replay must
/// tell the two apart, or it would pop the pending-question stack on the wrong task and lose track
/// of a question that's actually still open.
/// </param>
public sealed record TeamGraphTaskMetadata(
    string TopologyPositionId,
    string? MemberEntityId = null,
    string? MemberEntityKind = null,
    string[]? DependsOn = null,
    string? AskedByTopologyPositionId = null,
    bool IsRespondAgentResume = false
) : IGraphTaskMetadata;
