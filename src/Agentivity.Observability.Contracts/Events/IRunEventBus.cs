using System.Threading.Channels;

namespace Agentivity.Observability.Contracts.Events;

/// <summary>
/// Discriminant du moteur d'exécution source d'un run event.
/// </summary>
public enum RunKind
{
    Unknown,
    Workflow,
    Team,
    Agent,
}

/// <summary>
/// Result of a replay query on the event bus.
/// </summary>
public sealed record RunEventReplayResult(bool CanReplay, long CurrentVersion, IReadOnlyList<RunEvent> Events);

/// <summary>
/// Subscription handle returned by <see cref="IRunEventBus.Subscribe"/> and <see cref="IRunEventBus.SubscribeAll"/>.
/// Dispose to unsubscribe.
/// </summary>
public sealed class RunEventSubscription : IDisposable, IAsyncDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    public long StartVersion { get; }
    public ChannelReader<RunEvent> Reader { get; }

    public RunEventSubscription(long startVersion, ChannelReader<RunEvent> reader, Action dispose)
    {
        StartVersion = startVersion;
        Reader = reader;
        _dispose = dispose;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Discriminant sémantique des events de run unifiés.
/// Les noms suivent la convention snake_case utilisée par le team runtime et les endpoints SSE.
/// </summary>
public enum RunEventKind
{
    // ── Run lifecycle ──────────────────────────────────────────────────────────
    run_started,
    run_completed,
    run_failed,
    run_cancelled,
    run_updated,

    // ── Node / step ───────────────────────────────────────────────────────────
    node_started,
    node_completed,
    node_failed,
    node_updated,

    // ── Connection ────────────────────────────────────────────────────────────
    connection_updated,

    // ── Agent ─────────────────────────────────────────────────────────────────
    agent_started,
    agent_step_started,
    agent_step_completed,
    agent_completed,
    agent_failed,

    // ── LLM ───────────────────────────────────────────────────────────────────
    llm_call_started,
    llm_call_completed,

    // ── Tool ──────────────────────────────────────────────────────────────────
    tool_call_started,
    tool_call_completed,
    custom_widget_invoked,

    // ── Interaction (HIL / chat) ───────────────────────────────────────────────
    hil_gate_reached,
    hil_approved,
    hil_rejected,
    interaction_created,
    interaction_resolved,
    interaction_cancelled,
    message_sent,
    plan_created,
    replan_triggered,
    error_raised,

    // ── Chat ──────────────────────────────────────────────────────────────────
    chat_thread_created,
    chat_thread_updated,
}

/// <summary>
/// Unified run event envelope, regardless of the source engine (workflow, team, agent).
/// </summary>
public sealed record RunEvent(
    /// <summary>ID of the run (root or child) concerned by this event.</summary>
    string RunId,
    /// <summary>Definition ID (workflowId | teamId | agentId).</summary>
    string DefinitionId,
    RunKind RunKind,
    RunEventKind Kind,
    string EventName,
    DateTimeOffset OccurredAtUtc,
    /// <summary>Engine-specific serializable payload. May be null for simple lifecycle events.</summary>
    object? Payload = null,
    /// <summary>Whether this event marks the end of the run stream.</summary>
    bool Terminal = false,
    /// <summary>
    /// Execution identifier shared across all nested runs (workflow → team → agent).
    /// Matches <see cref="RunContext.ExecutionId"/>.
    /// </summary>
    string? ExecutionId = null,
    /// <summary>
    /// SSE channel identifier — routes this event to the correct stream subscription.
    /// Matches <see cref="RunContext.StreamId"/> = <see cref="ExecutionContext.StreamId"/>.
    /// Distinct from <see cref="RunId"/> and <see cref="ExecutionId"/>.
    /// </summary>
    string? StreamId = null
)
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("D");

    /// <summary>Monotonically increasing version assigned by the bus. Set by the bus on publish.</summary>
    public long Version { get; init; }
}

/// <summary>
/// Unified event bus exposed to all engines and observability bridges.
/// Supports replay, versioning, multi-subscriber, and global "all runs" stream.
/// </summary>
public interface IRunEventBus
{
    /// <summary>
    /// Publishes an event. Fire-and-forget — never throws.
    /// </summary>
    void Publish(RunEvent @event);

    /// <summary>
    /// Subscribes to events for a specific run.
    /// Returns a <see cref="RunEventSubscription"/> that must be disposed to unsubscribe.
    /// </summary>
    RunEventSubscription Subscribe(string runId);

    /// <summary>
    /// Subscribes to events for all runs (global stream).
    /// </summary>
    RunEventSubscription SubscribeAll();

    /// <summary>
    /// Replays buffered events for a specific run since <paramref name="lastEventVersion"/>.
    /// Pass null to get an empty replay (initial connect).
    /// </summary>
    RunEventReplayResult Replay(string runId, long? lastEventVersion, long maxVersionInclusive);

    /// <summary>
    /// Replays buffered events across all runs since <paramref name="lastEventVersion"/>.
    /// </summary>
    RunEventReplayResult ReplayAll(long? lastEventVersion, long maxVersionInclusive);

    /// <summary>
    /// Removes a run stream (cleanup after deletion). Completes all subscriber channels.
    /// </summary>
    void Remove(string runId);

    /// <summary>
    /// Subscribes to all events belonging to the given execution (matches <see cref="RunEvent.ExecutionId"/>).
    /// Returns a subscription that delivers events from all nested runs within the execution.
    /// </summary>
    /// <summary>
    /// Subscribes to the SSE channel identified by <paramref name="streamId"/> (<see cref="RunEvent.StreamId"/>).
    /// One channel per execution — <see cref="RunEvent.StreamId"/> comes from <c>ExecutionContext.StreamId</c>.
    /// </summary>
    RunEventSubscription SubscribeChannel(string streamId);

    RunEventReplayResult ReplayChannel(string streamId, long? lastEventVersion, long maxVersionInclusive);
}
