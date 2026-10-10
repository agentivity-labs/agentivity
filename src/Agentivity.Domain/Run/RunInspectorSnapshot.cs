using System.Text.Json.Serialization;

namespace Agentivity.Domain.Run;

/// <summary>
/// Generic debug/inspection snapshot for any run type: workflow, agent, or team.
/// Consumed exclusively by the Inspector panel in the UI — not a run status API.
/// Fetched on demand via GET /api/v1/runs/{runId}/inspector after sync_required SSE events.
/// </summary>
public sealed record RunInspectorSnapshot(Guid RunId, string EntityKind, RunStatus Status, IReadOnlyList<RunStepSnapshot> Steps, IReadOnlyList<RunStepTransitionSnapshot> Handoffs, IReadOnlyList<string> Logs, IReadOnlyList<string> Errors, string? TranscriptSummary = null, IReadOnlyList<RunStepCallSnapshot>? CallSequence = null)
{
    public static RunInspectorSnapshot Empty(Guid runId, string entityKind) => new(runId, entityKind, RunStatus.Pending, [], [], [], []);
}

/// <summary>
/// One entry per actual dispatch/call — unlike <see cref="RunStepSnapshot"/> (one entry per
/// team member/node, latest status only, upserted by id), a member/node called more than once
/// (e.g. a ManagerLed worker re-dispatched after answering an <c>ask_agent</c> question) appears
/// here once per call, in the order it actually happened. Lets the UI render a call-by-call
/// timeline instead of a static per-agent list. <see cref="AgentTopologyPositionId"/> joins back
/// to the matching <see cref="RunStepSnapshot.AgentTopologyPositionId"/> for display name/kind.
/// Team-run only for now (built from the team engine's own ordered task-output history) — null
/// <see cref="RunInspectorSnapshot.CallSequence"/> for workflow/standalone-agent runs, which don't
/// have this repeat-call shape. <see cref="Content"/> is this specific call's own output (null for
/// the synthetic "still running"/"waiting for input" entry appended for whatever's currently
/// active) — lets the UI show the right instance's data instead of falling back to
/// <see cref="RunStepSnapshot.Outputs"/>, which only ever holds the position's latest call.
/// </summary>
public sealed record RunStepCallSnapshot(string TaskId, string AgentTopologyPositionId, RunStatus Status, DateTimeOffset? OccurredAt, string? Content = null);

/// <summary>
/// Snapshot of a single execution step: a workflow node, a standalone agent, or a team member agent.
/// </summary>
public sealed record RunStepSnapshot(
    string Id,
    string Name,
    StepKind Kind,
    string? SubType,
    RunStatus Status,
    IReadOnlyDictionary<string, object?> Inputs,
    IReadOnlyDictionary<string, object?> Outputs,
    IReadOnlyList<string> Logs,
    string? Error = null,
    string? AgentTopologyPositionId = null,
    string? MemberEntityId = null,
    string? MemberEntityKind = null,
    DebugTraceItem? Trace = null
);

/// <summary>
/// Snapshot of a handoff between two team member agents.
/// Equivalent to a connection snapshot in workflow runs.
/// </summary>
public sealed record RunStepTransitionSnapshot(string FromStepId, string ToStepId, IReadOnlyDictionary<string, object?> Content, bool Traversed, DateTimeOffset Timestamp, string? FromPort = null, string? ToPort = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepKind
{
    Node,
    Agent,
}
