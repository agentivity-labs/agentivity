using System.Text.Json;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// Durable, engine-agnostic record of a single run — the persisted identity/correlation core of
/// the run tree (EPIC durable-execution, Story 1). One per run, all kinds (agent, team, workflow).
/// <para>
/// This is the "who am I, where am I in the tree, in what state" data, PLUS the engine's own
/// progress as an opaque payload (EPIC RunRecord-unifié, Story 1): <see cref="PayloadType"/>
/// selects which <see cref="IRecordConvertible{TSelf,TRecord}"/> shape to deserialize
/// <see cref="Payload"/> into (e.g. "team" → <c>TeamProgress</c>, "workflow" →
/// <c>WorkflowProgress</c>, "agent" → <c>AgentProgress</c>) — kept distinct from
/// <see cref="EntityKind"/> even though they mirror each other 1:1 today, as the seam for a
/// future payload schema version independent of the run kind. The store never inspects
/// <see cref="Payload"/>'s shape — only the owning engine does, via <c>FromRecord</c>.
/// </para>
/// <para>
/// The shared execution context (StreamId, thread) stays in <c>ExecutionRecord</c>. Values are
/// persisted, objects are rebuilt: this record never contains live references.
/// </para>
/// </summary>
public sealed record RunRecord(
    string RunId,
    string EntityKind,
    RunStatus Status,
    DateTimeOffset CreatedAt,
    string? EntityId = null,
    string? ExecutionId = null,
    string? ParentRunId = null,
    string? StepId = null,
    string? SessionId = null,
    string? Error = null,
    DateTimeOffset? CompletedAt = null,
    string? PayloadType = null,
    JsonElement? Payload = null,
    /// <summary>Serialized <see cref="RunContext.LastTranscriptSummary"/> — only ever populated on
    /// a root run's own record (child runs walk up to the root's live value in-process; after a
    /// restart, only the root needs to restore it). Null until the team's first member has
    /// completed a turn, or this record belongs to a non-root run.</summary>
    string? LastTranscriptSummary = null,
    /// <summary>Serialized <c>List&lt;RunStepSnapshot&gt;</c> — <see cref="RunContext.Steps"/> is
    /// otherwise in-memory only, so without this a process-restart reconstruction (<see
    /// cref="RunContext.FromRecord"/> — the only case where a run is rebuilt from this record
    /// rather than found live) silently discards the whole step history accumulated before the
    /// restart, leaving the Inspector showing only whatever ran after.</summary>
    JsonElement? StepsSnapshot = null,
    /// <summary>Serialized <c>List&lt;RunStepTransitionSnapshot&gt;</c> — <see cref="RunContext.Transitions"/>,
    /// same in-memory-only gap and same fix as <see cref="StepsSnapshot"/>.</summary>
    JsonElement? Transitions = null,
    /// <summary><see cref="RunContext.Logs"/> — same rationale as <see cref="StepsSnapshot"/>.</summary>
    IReadOnlyList<string>? Logs = null,
    /// <summary><see cref="RunContext.Errors"/> — same rationale as <see cref="StepsSnapshot"/>.</summary>
    IReadOnlyList<string>? Errors = null
);
