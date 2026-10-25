using System.Collections.Concurrent;
using System.Text.Json;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Repository;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;

namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// Mutable correlation context propagated through every level of the execution chain
/// (workflow → team → agent → nested composable) — AND the live, durable-backed state of the run
/// it belongs to (lifecycle, steps, transitions, logs, engine progress payload). Single object,
/// no separate "entry" type: a run has exactly one <see cref="RunContext"/>, registered once in
/// an <see cref="IRepository{TEntity,TRecord}"/>, converting to/from <see cref="RunRecord"/> for
/// persistence. Thread-safety for its own mutable state (steps, transitions, logs, lifecycle) is
/// owned here (an internal lock), not by whatever repository holds the reference.
/// <para>
/// Use <see cref="StartNew"/> to start a root run and <see cref="CreateChild"/> to derive a
/// child context. Inherited properties (e.g. <see cref="InteractionThreadId"/>) propagate
/// upward automatically when set on a child — no manual sync required.
/// </para>
/// </summary>
public sealed class RunContext : IRecordConvertible<RunRecord>
{
    private ExecutionContext? _execution;
    private string? _parentRunId;

    private string? _interactionThreadId;

    // Guards Steps/Transitions/Logs/Errors/Status/Error/CompletedAt/PayloadType/Payload/Control —
    // everything a repository's single coarse lock used to protect, now owned by the run itself
    // (no separate "entry" object, no external synchronization needed by callers).
    private readonly object _gate = new();

    private RunContext() { }

    // ── Core identity ─────────────────────────────────────────────────────────

    /// <summary>Unique id of this run (Guid "D" format).</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>workflowId | teamId | agentId depending on <see cref="Kind"/>.</summary>
    public string EntityId { get; set; } = string.Empty;

    public RunKind Kind { get; set; }

    /// <summary>
    /// RunId of the parent run; null for root runs.
    /// Computed from <see cref="Parent"/> when available; falls back to a stored value
    /// for contexts reconstructed without a parent object (e.g. <see cref="FromCorrelationContext"/>).
    /// </summary>
    public string? ParentRunId
    {
        get => Parent?.RunId ?? _parentRunId;
        set => _parentRunId = value;
    }

    /// <summary>nodeId or topologyPositionId in the parent that triggered this run.</summary>
    public string? StepId { get; set; }

    // ── Shared execution context ──────────────────────────────────────────────

    /// <summary>
    /// Shared context for the whole execution chain (HIL channel, telemetry, trace).
    /// Always reads from the root of the parent chain (lazy walk-up).
    /// Never null — every run belongs to an execution.
    /// </summary>
    public ExecutionContext Execution
    {
        get => Parent?.Execution ?? _execution!;
        set
        {
            if (Parent is not null)
                Parent.Execution = value;
            else
                _execution = value;
        }
    }

    // ── Run-local ports (reset on each Inherit call) ──────────────────────────

    public Observability.Contracts.IRunSessionPort? Session { get; set; }

    /// <summary>
    /// Explicit key for this run's conversation session, when the conversation must outlive the
    /// run (e.g. a team member keeps ONE session per topology position across HIL resumes and
    /// handoff revisits: <c>team-{teamRunId}-{topologyPositionId}</c>). Null → session proxies
    /// fall back to their default key. Run-local: not inherited by children.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Extra system-prompt text describing this run's orchestration context (e.g. a team member's
    /// routing table of available handoff targets). Run-local, not inherited by children — each
    /// invocation has its own, unlike <see cref="LastTranscriptSummary"/>. Null when not applicable.
    /// </summary>
    public string? TeamContextPrompt { get; set; }

    /// <summary>
    /// Explicitly assigned only by whichever code creates a run that should be a transcript scope
    /// boundary — a team run (its members read/write this one instance directly instead of each
    /// keeping a private summary; concurrent/fan-out members are the one exception — they get
    /// their own, since they don't actually collaborate and sharing one would race), or a
    /// standalone/workflow agent run (its own private scope, replacing the old per-session
    /// <c>SummaryStep</c> transcript). Null on any run that just participates in an ancestor's
    /// scope — ownership is decided once, explicitly, at creation, never inferred from
    /// <see cref="Kind"/> or resolved by walking to the absolute root of the parent chain.
    /// </summary>
    public TranscriptScope? OwnTranscriptScope { get; set; }

    /// <summary>
    /// Rolling prose summary of this run's transcript scope — resolved by walking up from this
    /// run (self included) to the nearest ancestor that owns a <see cref="TranscriptScope"/>
    /// (see <see cref="OwnTranscriptScope"/>), NOT the absolute root of the parent chain. For a
    /// team run, that's the team itself (every member reads/writes the same instance); for a
    /// standalone/workflow agent, that's itself (a private scope nobody else sees).
    /// <para>
    /// Compacted from the full conversation (messages + tool calls, never a hand-picked delta) by
    /// <c>AgentSessionCompaction</c> — shared between the team-level trigger (unconditionally,
    /// after every completed member turn — the next member needs fresh info immediately, there is
    /// nothing to window there) and the per-agent trigger (by size threshold, like the old
    /// <c>SummaryStep</c> — bounding one long-running turn's own context).
    /// </para>
    /// <para>
    /// If nothing in the chain owns a scope yet (a setter called before any creation site claimed
    /// ownership — a configuration gap, not expected in normal operation), the setter self-heals
    /// by creating one locally on this run rather than silently dropping the value.
    /// </para>
    /// </summary>
    public string? LastTranscriptSummary
    {
        get => ResolveTranscriptScope()?.Summary;
        set
        {
            var scope = ResolveTranscriptScope();
            if (scope is not null)
                scope.Summary = value;
            else
                OwnTranscriptScope = new TranscriptScope { Summary = value };
        }
    }

    private TranscriptScope? ResolveTranscriptScope()
    {
        var current = this;
        while (current is not null)
        {
            if (current.OwnTranscriptScope is not null)
                return current.OwnTranscriptScope;
            current = current.Parent;
        }
        return null;
    }

    public Observability.Contracts.IRunToolsPort? Tools { get; set; }
    public object? Policy { get; set; }
    public object? Guardrail { get; set; }

    // ── Inherited interaction state ───────────────────────────────────────────

    /// <summary>Reference to the parent context; null for root runs.</summary>
    public RunContext? Parent { get; private set; }

    /// <summary>
    /// Active interaction thread for this execution (chat, email, Slack, …).
    /// Always reads from the root of the parent chain (lazy walk-up).
    /// Setting on any descendant writes to the root so all siblings share the same value.
    /// </summary>
    public string? InteractionThreadId
    {
        get => Parent?.InteractionThreadId ?? _interactionThreadId;
        set
        {
            if (Parent is not null)
                Parent.InteractionThreadId = value;
            else
                _interactionThreadId = value;
        }
    }

    /// <summary>True when this context was created via <see cref="Resume"/> — i.e. it's a re-invocation
    /// of an existing run (e.g. after HIL). Used to skip re-publishing the initial user message.</summary>
    public bool IsResume { get; private set; }

    /// <summary>
    /// Marks this SAME instance as re-entering after a pause — never construct a second
    /// <see cref="RunContext"/> for an existing run. A run has exactly one live context, registered
    /// once in the repository's cache; building a parallel copy for resume (even one merged back
    /// via the old, now-removed <c>ReconcileForResume</c>) creates two independently-mutable instances that
    /// silently diverge — mutations on one (step statuses, transcript, lifecycle state) are
    /// invisible through the other, which is exactly what the cache-backed repository returns to
    /// every other reader. <paramref name="execution"/> only needs passing when this instance was
    /// itself just reconstructed from a durable record with a minimal placeholder (see
    /// <see cref="FromRecord"/>) and the real one (carrying e.g. StreamId) is now available.
    /// </summary>
    public void PrepareForResume(ExecutionContext? execution = null)
    {
        IsResume = true;
        if (execution is not null)
            Execution = execution;
    }

    // ── Lifecycle + engine progress (absorbed from the former RunEntry) ────────

    public RunStatus Status { get; set; } = RunStatus.Running;
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Discriminant selecting which <see cref="IRecordConvertible{TRecord}"/> shape <see cref="Payload"/> is.</summary>
    public string? PayloadType { get; set; }

    /// <summary>Opaque, engine-owned progress payload — set only via <see cref="SetPayload"/> so
    /// every RunRecord write-through (lifecycle AND payload) goes through one path; never
    /// overwritten by a lifecycle-only transition.</summary>
    public JsonElement? Payload { get; set; }

    /// <summary>Live pause/resume/cancel primitive for this run. Never persisted (rebuilt fresh
    /// on process restart by whichever engine re-enters the run).</summary>
    public RunControl? Control { get; set; }

    // Keyed by step id for fast upsert.
    public Dictionary<string, RunStepEntry> Steps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RunStepTransitionSnapshot> Transitions { get; } = [];
    public List<string> Logs { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>
    /// What the run's tools keep for the time of the run (an In-Memory Vector Store's texts), shared by all of them. Saved with the
    /// run's record, so a run paused and resumed — even after a restart — finds it again. Its values are plain data: read back
    /// after a restart, each is a <see cref="JsonElement"/> its tool turns back into its own shape.
    /// </summary>
    public ConcurrentDictionary<string, object?> ToolState { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Thread-safe mutation (was IRunRepository's UpsertStep/AddTransition/etc.) ─────────────

    /// <summary>Inserts or merges a step (matched by id). Existing fields not touched by <paramref name="configure"/> are preserved.</summary>
    public void UpsertStep(string stepId, Action<RunStepEntry> configure)
    {
        lock (_gate)
        {
            if (!Steps.TryGetValue(stepId, out var step))
            {
                step = new RunStepEntry { Id = stepId };
                Steps[stepId] = step;
            }
            configure(step);
        }
    }

    public void AddTransition(RunStepTransitionSnapshot transition)
    {
        lock (_gate)
        {
            Transitions.Add(transition);
        }
    }

    /// <summary>Inserts or updates a transition by (FromStepId, ToStepId) key.</summary>
    public void UpsertTransition(RunStepTransitionSnapshot transition)
    {
        lock (_gate)
        {
            var idx = Transitions.FindIndex(h => string.Equals(h.FromStepId, transition.FromStepId, StringComparison.OrdinalIgnoreCase) && string.Equals(h.ToStepId, transition.ToStepId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                Transitions[idx] = transition;
            else
                Transitions.Add(transition);
        }
    }

    public void AppendStepLog(string stepId, string message)
    {
        lock (_gate)
        {
            if (Steps.TryGetValue(stepId, out var step))
                step.Logs.Add(message);
            else
                Logs.Add($"[{stepId}] {message}");
        }
    }

    public void AppendError(string message)
    {
        lock (_gate)
        {
            Errors.Add(message);
        }
    }

    public void SetStepTrace(string stepId, DebugTraceItem? trace)
    {
        lock (_gate)
        {
            if (!Steps.TryGetValue(stepId, out var step))
            {
                step = new RunStepEntry { Id = stepId };
                Steps[stepId] = step;
            }
            step.Trace = trace;
        }
    }

    public DebugTraceItem? GetStepTrace(string stepId)
    {
        lock (_gate)
        {
            return Steps.TryGetValue(stepId, out var step) ? step.Trace : null;
        }
    }

    /// <summary>Sets lifecycle status/error/completion. Auto-stamps <see cref="CompletedAt"/> on a terminal status if not already set.</summary>
    public void SetStatus(RunStatus status, string? error = null, DateTimeOffset? completedAt = null)
    {
        lock (_gate)
        {
            Status = status;
            if (error is not null)
                Error = error;
            if (completedAt is not null)
                CompletedAt = completedAt;
            if (status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
                CompletedAt ??= DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Sets the engine-owned progress payload — the only writer of <see cref="PayloadType"/>/<see cref="Payload"/>.</summary>
    public void SetPayload(string payloadType, JsonElement payload)
    {
        lock (_gate)
        {
            PayloadType = payloadType;
            Payload = payload;
        }
    }

    /// <summary>Pauses the run if currently <see cref="RunStatus.Running"/> and a <see cref="Control"/> is attached. Returns false otherwise (no-op).</summary>
    public bool TryPause()
    {
        lock (_gate)
        {
            if (Status != RunStatus.Running || Control is null)
                return false;
            Control.Pause();
            Status = RunStatus.Paused;
            return true;
        }
    }

    /// <summary>Resumes the run if currently <see cref="RunStatus.Paused"/> and a <see cref="Control"/> is attached. Returns false otherwise (no-op).</summary>
    public bool TryResume()
    {
        lock (_gate)
        {
            if (Status != RunStatus.Paused || Control is null)
                return false;
            Control.Resume();
            Status = RunStatus.Running;
            return true;
        }
    }

    /// <summary>Cancels the run unless already terminal. Returns false if already terminal (no-op).</summary>
    public bool TryCancel()
    {
        lock (_gate)
        {
            if (Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
                return false;
            Control?.Cancel();
            Status = RunStatus.Cancelled;
            CompletedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public RunInspectorSnapshot ToInspectorSnapshot()
    {
        lock (_gate)
        {
            var runId = Guid.TryParse(RunId, out var g) ? g : Guid.Empty;
            return new RunInspectorSnapshot(RunId: runId, EntityKind: Kind.ToString(), Status: Status, Steps: Steps.Values.Select(s => s.ToSnapshot()).ToList(), Handoffs: Transitions, Logs: Logs, Errors: Errors, TranscriptSummary: LastTranscriptSummary, CallSequence: BuildCallSequence());
        }
    }

    /// <summary>
    /// Reads the team engine's own ordered per-task output history straight out of <see cref="Payload"/>
    /// (its "outputs" field — <c>TeamProgress.Outputs</c>'s JSON shape) by field name, never through
    /// the strongly-typed <c>TeamProgress</c> record: that type lives in
    /// <c>Agentivity.Infrastructure.Agentic.Team</c>, which depends on THIS project, so this project
    /// cannot reference it back. Same by-name JSON reasoning as <c>RunRepository.OnEvent</c>'s own
    /// doc comment. Appends one synthetic "still running" entry for whatever's currently active in
    /// <see cref="Steps"/> (that position's most recent call has no output yet, so it can't appear
    /// in the "outputs" array at all) so the UI can highlight "we are here" at the end of the
    /// sequence. Returns null for a non-team run (no "outputs" field in its payload shape).
    /// </summary>
    private List<RunStepCallSnapshot>? BuildCallSequence()
    {
        if (Payload is not { } payload || !payload.TryGetProperty("outputs", out var outputsEl) || outputsEl.ValueKind != JsonValueKind.Array)
            return null;

        var sequence = new List<RunStepCallSnapshot>();
        foreach (var entry in outputsEl.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var taskId = entry.TryGetProperty("taskId", out var t) ? t.GetString() : null;
            var agentId = entry.TryGetProperty("agentId", out var a) ? a.GetString() : null;
            if (taskId is null || agentId is null) continue;
            var occurredAt = entry.TryGetProperty("occurredAt", out var o) && o.TryGetDateTimeOffset(out var dt) ? dt : (DateTimeOffset?)null;
            var content = entry.TryGetProperty("content", out var c) ? c.GetString() : null;
            sequence.Add(new RunStepCallSnapshot(taskId, agentId, RunStatus.Completed, occurredAt, content));
        }

        // Not just Running: a member's turn that ends by asking the actual human a question
        // (agent_next_step's needs_input, with a real HIL channel) SUSPENDS the underlying task
        // rather than completing it — its step status is WaitingForInput, never Running, and
        // completion (so its own entry in "outputs" above) only lands once the human answers.
        // Missing this here meant a member currently asking the human a question — a real,
        // in-progress call — simply never appeared in the sequence at all.
        foreach (var step in Steps.Values)
        {
            if (step.AgentTopologyPositionId is { } activeId &&
                step.Status is RunStatus.Running or RunStatus.WaitingForInput or RunStatus.Paused)
            {
                sequence.Add(new RunStepCallSnapshot($"{RunId}-current-{activeId}", activeId, step.Status, null));
            }
        }

        return sequence;
    }

    // ── Durable conversion (IRecordConvertible) ────────────────────────────────

    public RunRecord ToRecord()
    {
        lock (_gate)
        {
            // Only a run that actually owns a transcript scope carries the summary in its own
            // record — participants resolve theirs live, in-process, from whichever ancestor owns
            // one; only the owner itself needs to restore it after a restart.
            var lastTranscriptSummary = OwnTranscriptScope?.Summary;
            var stepsSnapshot = Steps.Count > 0 ? JsonSerializer.SerializeToElement(Steps.Values.Select(s => s.ToSnapshot()).ToList()) : (JsonElement?)null;
            var transitionsSnapshot = Transitions.Count > 0 ? JsonSerializer.SerializeToElement(Transitions) : (JsonElement?)null;
            return new(RunId, Kind.ToString(), Status, CreatedAt, EntityId, Execution.ExecutionId, ParentRunId, StepId, SessionId, Error, CompletedAt, PayloadType, Payload, lastTranscriptSummary, stepsSnapshot, transitionsSnapshot, Logs.Count > 0 ? [.. Logs] : null, Errors.Count > 0 ? [.. Errors] : null, ToolState.IsEmpty ? null : JsonSerializer.SerializeToElement(ToolState.ToDictionary(e => e.Key, e => e.Value), JsonSerializerOptions.Web));
        }
    }

    /// <summary>
    /// Resolves the one live <see cref="RunContext"/> for an already-resolved <see
    /// cref="Executions.ExecutionContext"/>. Two cases only, never blurred into one:
    /// <list type="bullet">
    /// <item>the execution already has a <see cref="Executions.ExecutionContext.CurrentRunId"/> —
    /// this IS an existing run, so it MUST be found; if it isn't, that is a genuine inconsistency
    /// (a lost/corrupted record, never a legitimate "start fresh" case) and this throws rather than
    /// silently minting a replacement that would orphan whatever the real run was doing;</item>
    /// <item>the execution has none yet — this is a genuinely new run, created (and registered)
    /// here for the first time.</item>
    /// </list>
    /// A found run's <c>RunId</c> is never touched, re-derived, or copied — the exact stored
    /// instance is returned. A newly-created run always gets a plain, freshly-minted
    /// <c>Guid.NewGuid()</c> — the sole exception is <see cref="RunKind.Agent"/>, where <c>RunId ==
    /// ExecutionId</c> is a deliberate, documented, API-facing invariant since Story 1 (see <see
    /// cref="StartNew(RunKind,string,string?)"/>, <c>SessionsEndpoints.cs</c>,
    /// <c>AgentProxyFactory.SessionKey</c>) — not a derivation introduced here, and not something
    /// any other run kind does.
    /// <para>
    /// Replaces the old two-step <c>Join</c>-then-<see cref="FindOrRegister"/> dance: <c>Join</c>
    /// built a throwaway candidate on every call and relied on its <c>RunId</c> happening to match
    /// the live run's for the lookup to succeed, silently falling through to "create new" whenever
    /// it didn't. For Team/Workflow it minted an unconditional new GUID, so it could never match —
    /// confirmed in production, every plain chat message sent to a live Team run spun up a second,
    /// entirely disconnected run under the same execution, silently orphaning the original
    /// (mid-dispatch, or waiting on a member's own HIL gate) while the new one started from a blank
    /// context. A run has exactly one live instance; this either returns that exact instance or
    /// creates it — it never guesses at a copy.
    /// </para>
    /// </summary>
    public static async ValueTask<RunContext> GetOrCreate(IRepository<RunContext, RunRecord> repository, RunKind kind, string entityId, Executions.ExecutionContext execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(execution);

        if (execution.CurrentRunId is { } currentRunId)
        {
            return await repository.FindAsync(currentRunId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Execution '{execution.ExecutionId}' records CurrentRunId '{currentRunId}' but no such run exists — refusing to silently start a replacement run.");
        }

        var runId = kind == RunKind.Agent ? execution.ExecutionId : Guid.NewGuid().ToString("D");
        var fresh = new RunContext { RunId = runId, EntityId = entityId, Kind = kind, Execution = execution };
        await repository.AddAsync(fresh, ct).ConfigureAwait(false);
        return fresh;
    }

    /// <summary>
    /// Registers <paramref name="candidate"/> as the run's live context unless one is already
    /// registered under <paramref name="candidate"/>'s <see cref="RunId"/>, in which case the
    /// EXISTING live instance is returned unchanged and <paramref name="candidate"/> is discarded
    /// untouched. Replaces the old <c>FindAsync</c> + <c>ReconcileForResume</c> + <c>AddAsync</c>
    /// dance: that pattern always built a second, independently-mutable <see cref="RunContext"/>
    /// for an already-live run and copied an incomplete field list into it (<c>Session</c>/
    /// <c>Tools</c>/<c>Memory</c>/<c>Policy</c>/<c>Guardrail</c>/<c>TeamContextPrompt</c> never
    /// round-trip through <see cref="RunRecord"/> at all — they were silently dropped on every
    /// join). A run has exactly one live context; this never builds a competing copy of it.
    /// </summary>
    public static async ValueTask<RunContext> FindOrRegister(IRepository<RunContext, RunRecord> repository, RunContext candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(candidate);
        var existing = await repository.FindAsync(candidate.RunId, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;
        await repository.AddAsync(candidate, ct).ConfigureAwait(false);
        return candidate;
    }

    /// <summary>Reconstructs a root <see cref="RunContext"/> from its durable <see cref="RunRecord"/>
    /// (process-restart fallback). No <see cref="Parent"/>/live ports — those are never persisted.</summary>
    public static RunContext FromRecord(RunRecord r)
    {
        var ctx = new RunContext
        {
            RunId = r.RunId,
            EntityId = r.EntityId ?? string.Empty,
            Kind = Enum.TryParse<RunKind>(r.EntityKind, out var k) ? k : default,
            _parentRunId = r.ParentRunId,
            StepId = r.StepId,
            SessionId = r.SessionId,
            Status = r.Status,
            Error = r.Error,
            CreatedAt = r.CreatedAt,
            CompletedAt = r.CompletedAt,
            PayloadType = r.PayloadType,
            Payload = r.Payload,
            _execution = ExecutionContext.CreateMinimal(r.ExecutionId),
            OwnTranscriptScope = r.LastTranscriptSummary is not null ? new TranscriptScope { Summary = r.LastTranscriptSummary } : null,
        };
        RestoreStepHistory(ctx, r);
        return ctx;
    }

    /// <summary>Restore logic for <see cref="FromRecord"/> (its only caller — the process-restart
    /// reconstruction path) — deserializes <see cref="RunRecord.StepsSnapshot"/>/<see cref="RunRecord.Transitions"/> back
    /// into <paramref name="ctx"/>'s live, mutable collections, and appends
    /// <see cref="RunRecord.Logs"/>/<see cref="RunRecord.Errors"/>.</summary>
    private static void RestoreStepHistory(RunContext ctx, RunRecord r)
    {
        if (r.StepsSnapshot is { } stepsJson)
        {
            var snapshots = JsonSerializer.Deserialize<List<RunStepSnapshot>>(stepsJson.GetRawText());
            if (snapshots is not null)
                foreach (var snapshot in snapshots)
                    ctx.Steps[snapshot.Id] = RunStepEntry.FromSnapshot(snapshot);
        }

        if (r.Transitions is { } transitionsJson)
        {
            var transitions = JsonSerializer.Deserialize<List<RunStepTransitionSnapshot>>(transitionsJson.GetRawText());
            if (transitions is not null)
                ctx.Transitions.AddRange(transitions);
        }

        if (r.Logs is { } logs)
            ctx.Logs.AddRange(logs);

        if (r.Errors is { } errors)
            ctx.Errors.AddRange(errors);

        if (r.ToolState is { ValueKind: JsonValueKind.Object } toolState)
            foreach (var entry in toolState.EnumerateObject())
                ctx.ToolState[entry.Name] = entry.Value.Clone();
    }

    /// <summary>
    /// Derives a child <see cref="RunContext"/> from this one.
    /// Propagates <see cref="Execution"/> (and therefore ExecutionId + StreamId) and
    /// <see cref="InteractionThreadId"/> via the Parent chain. Run-local ports are reset.
    /// </summary>
    /// <param name="runId">
    /// RunId for the child. Null (new run) → a new Guid is generated. Non-null → the child
    /// re-enters an EXISTING run whose id was restored from persistence (task metadata,
    /// run record) — never a derived/computed value.
    /// </param>
    /// <param name="isResume">
    /// True when the child re-enters an existing suspended run (nested team/workflow member
    /// resumed through the cascade) — the child engine then loads its restore point instead
    /// of starting fresh, exactly like a root created via <see cref="Resume"/>.
    /// </param>
    public RunContext CreateChild(RunKind childKind, string childEntityId, string? stepId = null, string? runId = null, bool isResume = false)
    {
        // All inherited properties (Execution, InteractionThreadId, ParentRunId) are read
        // dynamically from Parent via the lazy getters — nothing to copy explicitly.
        return new RunContext
        {
            RunId = runId ?? Guid.NewGuid().ToString("D"),
            EntityId = childEntityId,
            Kind = childKind,
            StepId = stepId,
            Parent = this,
            IsResume = isResume,
        };
    }

    /// <summary>
    /// Produces an <see cref="ExecutionCorrelationContext"/> for telemetry and the workflow engine.
    /// </summary>
    public ExecutionCorrelationContext ToCorrelationContext() => new(RunId: Guid.TryParse(RunId, out var g) ? g : Guid.NewGuid(), EntityId: EntityId, TraceId: Execution.ExecutionId, SpanId: RunId, ParentSpanId: ParentRunId, NodeRunId: Kind == RunKind.Agent ? RunId : ParentRunId, NodeExecutionId: StepId);

    /// <summary>
    /// Reconstructs a <see cref="RunContext"/> from an <see cref="ExecutionCorrelationContext"/>.
    /// </summary>
    public static RunContext FromCorrelationContext(ExecutionCorrelationContext ctx, RunKind kind = RunKind.Workflow) =>
        new()
        {
            RunId = ctx.RunId.ToString("D"),
            EntityId = ctx.EntityId,
            Kind = kind,
            Execution = ExecutionContext.CreateMinimal(ctx.TraceId),
            ParentRunId = ctx.NodeRunId ?? ctx.ParentSpanId,
            StepId = ctx.NodeExecutionId,
        };

    /// <summary>
    /// Creates a root <see cref="RunContext"/> (no parent) for a new run.
    /// If <paramref name="executionId"/> is provided the run joins an existing execution.
    /// </summary>
    public static RunContext StartNew(RunKind kind, string entityId, string? executionId = null)
    {
        if (kind == RunKind.Agent)
        {
            // Standalone agents reuse the same id for RunId and ExecutionId so that
            // StandaloneSessionProxy (keyed by RunId) restores history across turns.
            var id = executionId ?? Guid.NewGuid().ToString("D");
            return new RunContext
            {
                RunId = id,
                EntityId = entityId,
                Kind = kind,
                Execution = ExecutionContext.CreateMinimal(id),
            };
        }

        return new RunContext
        {
            RunId = Guid.NewGuid().ToString("D"),
            EntityId = entityId,
            Kind = kind,
            Execution = ExecutionContext.CreateMinimal(executionId),
        };
    }

    /// <summary>
    /// Reconstructs a root <see cref="RunContext"/> for a suspended run.
    /// Pass the original <paramref name="execution"/> from <c>IExecutionRepository.TryGetAsync</c>
    /// to preserve the same <see cref="StreamId"/> so SSE channel events reach the same subscriber.
    /// </summary>
    public static RunContext Resume(string existingRunId, RunKind kind, string entityId, ExecutionContext? execution = null) =>
        new()
        {
            RunId = existingRunId,
            EntityId = entityId,
            Kind = kind,
            Execution = execution ?? ExecutionContext.CreateMinimal(),
            IsResume = true,
        };
}

/// <summary>Mutable step entry — projects to immutable <see cref="RunStepSnapshot"/> on demand.</summary>
public sealed class RunStepEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StepKind Kind { get; set; }
    public string? SubType { get; set; }
    public RunStatus Status { get; set; }
    public Dictionary<string, object?> Inputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Outputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Logs { get; } = [];
    public string? Error { get; set; }
    public string? AgentTopologyPositionId { get; set; }
    public string? MemberEntityId { get; set; }
    public string? MemberEntityKind { get; set; }

    public DebugTraceItem? Trace { get; set; }

    public RunStepSnapshot ToSnapshot() => new(Id, Name, Kind, SubType, Status, Inputs, Outputs, Logs, Error, AgentTopologyPositionId, MemberEntityId, MemberEntityKind, Trace);

    /// <summary>Reconstructs a mutable entry from its immutable snapshot — the durable-restore
    /// counterpart of <see cref="ToSnapshot"/>, used when rehydrating <see cref="RunContext.Steps"/>
    /// from a <see cref="RunRecord"/> (process restart or HIL resume).</summary>
    public static RunStepEntry FromSnapshot(RunStepSnapshot s)
    {
        var entry = new RunStepEntry
        {
            Id = s.Id,
            Name = s.Name,
            Kind = s.Kind,
            SubType = s.SubType,
            Status = s.Status,
            Inputs = new Dictionary<string, object?>(s.Inputs, StringComparer.OrdinalIgnoreCase),
            Outputs = new Dictionary<string, object?>(s.Outputs, StringComparer.OrdinalIgnoreCase),
            Error = s.Error,
            AgentTopologyPositionId = s.AgentTopologyPositionId,
            MemberEntityId = s.MemberEntityId,
            MemberEntityKind = s.MemberEntityKind,
            Trace = s.Trace,
        };
        entry.Logs.AddRange(s.Logs);
        return entry;
    }
}
