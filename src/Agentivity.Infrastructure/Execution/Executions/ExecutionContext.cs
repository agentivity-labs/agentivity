using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Repository;
using Agentivity.Observability.Contracts;

namespace Agentivity.Infrastructure.Execution.Executions;

/// <summary>
/// Contexte partagé sur toute la chaîne d'exécution (workflow → team → agent → sous-workflow) —
/// ET l'état vivant, durable-backed de l'exécution à laquelle il appartient (entité, input, état,
/// canaux HIL/chat associés). Une seule instance est créée à la racine et propagée inchangée via
/// <c>RunContext.CreateChild</c>.
/// <para>
/// Comme <see cref="RunContext"/>/<see cref="RunRecord"/> : pas de type "entrée" séparé — une
/// exécution a exactement un <see cref="ExecutionContext"/>, convertissant vers/depuis
/// <see cref="ExecutionRecord"/> via <see cref="IRecordConvertible{TRecord}"/>. Thread-safety pour
/// son propre état mutable (Channels, State) possédée ici (verrou interne), pas par le repository
/// qui détient la référence.
/// </para>
/// </summary>
public sealed class ExecutionContext : IRecordConvertible<ExecutionRecord>
{
    private readonly object _gate = new();
    internal string _streamId = Guid.NewGuid().ToString("D");

    /// <summary>
    /// Identifiant unique de l'exécution racine — partagé par tous les runs de la chaîne.
    /// Correspond à <c>RunContext.Execution.ExecutionId</c>.
    /// </summary>
    public string ExecutionId { get; }

    /// <summary>
    /// Identifiant unique du canal SSE pour cette exécution.
    /// Généré une fois à la création — tous les runs de la chaîne partagent ce canal.
    /// Distinct de <see cref="ExecutionId"/> (corrélation) et de <c>RunContext.RunId</c>.
    /// </summary>
    public string StreamId => _streamId;

    /// <summary>
    /// Canal HIL partagé sur toute la chaîne d'exécution.
    /// Null si aucun canal HIL n'est configuré pour cette exécution.
    /// </summary>
    public IExecutionHilChannel? HilChannel { get; }

    /// <summary>
    /// Surface de télémétrie OTel partagée sur toute la chaîne.
    /// Null si la télémétrie n'est pas configurée pour cette exécution.
    /// </summary>
    public IExecutionTelemetry? Telemetry { get; }

    /// <summary>
    /// Surface de trace / debug partagée sur toute la chaîne.
    /// Null si la trace n'est pas activée pour cette exécution.
    /// </summary>
    public IExecutionTrace? Trace { get; }

    // ── Lifecycle + durable fields (absorbed from ExecutionRecord) ─────────────

    /// <summary>workflowId | teamId | agentId. Empty until the entity is known (populated by the
    /// call site that starts the execution — not known at construction time).</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>"agent" | "team" | "workflow".</summary>
    public string EntityKind { get; set; } = string.Empty;

    public string Input { get; set; } = string.Empty;

    /// <summary>"active" | "paused" | "completed" | "failed".</summary>
    public string State { get; set; } = "active";

    public string? CurrentRunId { get; set; }
    public List<string> RunIds { get; } = [];

    /// <summary>Chat thread identifier — independent from RunId, ExecutionId and StreamId.
    /// Persisted so the interaction thread can be restored on HIL resume without creating a new thread.</summary>
    public string? InteractionThreadId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Who started it: seeded once, with its entity (see <see cref="AttachRun"/>); its runs, children included, run as them.</summary>
    public Agentivity.Infrastructure.Identity.Caller? StartedBy { get; private set; }

    /// <summary>The space it belongs to: the space of what it runs.</summary>
    public string SpaceId { get; private set; } = Agentivity.Infrastructure.Storage.Workspace.WorkspaceSpaces.Default;

    /// <summary>What started it when it was not a person asking (a schedule: <c>schedule:&lt;id&gt;</c>); null otherwise.</summary>
    public string? Origin { get; private set; }

    /// <summary>Notes what starts a new execution; an execution already started keeps what it had.</summary>
    public void NoteOrigin(string? origin)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(EntityId) && !string.IsNullOrWhiteSpace(origin))
                Origin = origin;
        }
    }

    /// <summary>
    /// Places a new execution in <paramref name="spaceId"/>, the space of what it runs; an execution already started (a
    /// continuation) stays where it is.
    /// </summary>
    public void PlaceIn(string spaceId)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(EntityId) && !string.IsNullOrWhiteSpace(spaceId))
                SpaceId = spaceId;
        }
    }

    // channelType → threadId
    private readonly Dictionary<string, string> _channels = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> Channels => _channels;

    /// <summary>Thread-safe upsert of a channel → thread id mapping. Stamps <see cref="UpdatedAt"/>.</summary>
    public void UpdateChannel(string channelType, string threadId)
    {
        lock (_gate)
        {
            _channels[channelType] = threadId;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Records that <paramref name="runId"/> is now driving this execution — called once per
    /// <c>Start*Async</c> call (including re-joining an existing execution, e.g. HIL resume).
    /// Entity identity (<see cref="EntityId"/>/<see cref="EntityKind"/>/<see cref="Input"/>) and
    /// <see cref="State"/> are only seeded the first time (an empty <see cref="EntityId"/> means
    /// this is a brand-new execution) — a re-join must not clobber the accumulated history.
    /// </summary>
    public void AttachRun(string entityId, string entityKind, string input, string runId, Agentivity.Infrastructure.Identity.Caller? startedBy = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(EntityId))
            {
                EntityId = entityId;
                EntityKind = entityKind;
                Input = input;
                State = "active";
                StartedBy = startedBy;
            }
            CurrentRunId = runId;
            if (!RunIds.Contains(runId))
                RunIds.Add(runId);
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// The state of an execution, from the state of its top run: running is "active", waiting (for a person's answer, or
    /// paused) is "paused", and ended is "completed", "failed" or "cancelled". The one rule every path that starts or takes
    /// up a run again uses, whatever the kind of the run.
    /// </summary>
    public static string StateOf(Agentivity.Domain.Run.RunStatus status) =>
        status switch
        {
            Agentivity.Domain.Run.RunStatus.Pending or Agentivity.Domain.Run.RunStatus.Running => "active",
            Agentivity.Domain.Run.RunStatus.Paused or Agentivity.Domain.Run.RunStatus.WaitingForInput => "paused",
            Agentivity.Domain.Run.RunStatus.Completed => "completed",
            Agentivity.Domain.Run.RunStatus.Cancelled => "cancelled",
            _ => "failed",
        };

    /// <summary>Thread-safe state transition ("active" | "paused" | "completed" | "failed" | "cancelled"). Stamps <see cref="UpdatedAt"/>.</summary>
    public void SetState(string state)
    {
        lock (_gate)
        {
            State = state;
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Resolves the live <see cref="ExecutionContext"/> for <paramref name="executionId"/> if one
    /// is already registered (a continuation), otherwise creates and registers a brand-new one.
    /// Replaces the old <c>StartNew</c>-then-<c>ReconcileForJoin</c> dance: that pattern always
    /// built a SECOND <see cref="ExecutionContext"/> for an already-live execution and copied an
    /// incomplete field list into it — <see cref="HilChannel"/>/<see cref="Telemetry"/>/<see
    /// cref="Trace"/> have no setters at all, so they were silently lost on every join, and <see
    /// cref="StreamId"/> being regenerated on every join was the concrete, observed bug (a chat
    /// message published on the execution's original stream while a follow-up HIL gate opened on
    /// the join's fresh one — two different SSE channels for what the client sees as one
    /// conversation). This never builds a copy: an existing execution's live object is returned
    /// exactly as-is.
    /// </summary>
    public static async ValueTask<ExecutionContext> FindOrCreate(IRepository<ExecutionContext, ExecutionRecord> repository, string? executionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (executionId is not null)
        {
            var existing = await repository.FindAsync(executionId, ct).ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }
        var created = CreateMinimal(executionId);
        await repository.AddAsync(created, ct).ConfigureAwait(false);
        return created;
    }

    public ExecutionContext(string executionId, IExecutionHilChannel? hilChannel = null, IExecutionTelemetry? telemetry = null, IExecutionTrace? trace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ExecutionId = executionId;
        HilChannel = hilChannel;
        Telemetry = telemetry;
        Trace = trace;
    }

    /// <summary>
    /// Crée un <see cref="ExecutionContext"/> minimal sans aucune surface optionnelle.
    /// Utilisé dans les contextes où télémétrie, HIL et trace ne sont pas requis.
    /// </summary>
    public static ExecutionContext CreateMinimal(string? executionId = null) => new(executionId ?? Guid.NewGuid().ToString("D"));

    /// <summary>
    /// Reconstruit un <see cref="ExecutionContext"/> depuis des valeurs persistées (ex: reprise après HIL).
    /// Préserve l'<paramref name="executionId"/> et le <paramref name="streamId"/> d'origine
    /// pour que le canal SSE frontend reste connecté au bon stream.
    /// </summary>
    public static ExecutionContext Reconstruct(string executionId, string? streamId = null)
    {
        var ctx = new ExecutionContext(executionId ?? Guid.NewGuid().ToString("D"));
        if (!string.IsNullOrWhiteSpace(streamId))
            ctx._streamId = streamId;
        return ctx;
    }

    // ── Durable conversion (IRecordConvertible) ────────────────────────────────

    public ExecutionRecord ToRecord()
    {
        lock (_gate)
        {
            return new ExecutionRecord
            {
                ExecutionId = ExecutionId,
                EntityId = EntityId,
                EntityKind = EntityKind,
                Input = Input,
                State = State,
                Channels = new Dictionary<string, string>(_channels, StringComparer.OrdinalIgnoreCase),
                CurrentRunId = CurrentRunId,
                RunIds = [.. RunIds],
                StreamId = StreamId,
                InteractionThreadId = InteractionThreadId,
                StartedBy = StartedBy,
                SpaceId = SpaceId,
                Origin = Origin,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
            };
        }
    }

    /// <summary>Reconstructs an <see cref="ExecutionContext"/> from its durable <see cref="ExecutionRecord"/>
    /// (process-restart fallback). Preserves the same <see cref="StreamId"/> so SSE subscriptions still match.</summary>
    public static ExecutionContext FromRecord(ExecutionRecord r)
    {
        var ctx = Reconstruct(r.ExecutionId, r.StreamId);
        ctx.EntityId = r.EntityId;
        ctx.EntityKind = r.EntityKind;
        ctx.Input = r.Input;
        ctx.State = r.State;
        foreach (var (channelType, threadId) in r.Channels)
            ctx._channels[channelType] = threadId;
        ctx.CurrentRunId = r.CurrentRunId;
        ctx.RunIds.AddRange(r.RunIds);
        ctx.InteractionThreadId = r.InteractionThreadId;
        ctx.StartedBy = r.StartedBy;
        ctx.Origin = r.Origin;
        ctx.SpaceId = string.IsNullOrWhiteSpace(r.SpaceId) ? Agentivity.Infrastructure.Storage.Workspace.WorkspaceSpaces.Default : r.SpaceId;
        ctx.CreatedAt = r.CreatedAt;
        ctx.UpdatedAt = r.UpdatedAt;
        return ctx;
    }
}
