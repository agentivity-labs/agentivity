namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Decision engine for a single graph execution. Owned by <see cref="GraphSession"/>.
/// The only entity allowed to create <see cref="GraphTask"/> instances.
/// Receives its own <see cref="GraphSession"/> at each decision point —
/// never a reference to the global <see cref="GraphHost"/>.
/// </summary>
public abstract class GraphOrchestrator
{
    /// <summary>Domain state shared across all tasks in this run.</summary>
    public abstract GraphExecutionContext Context { get; }

    /// <summary>
    /// Called once before scheduling begins. Returns the initial tasks to inject.
    /// </summary>
    public abstract ValueTask<IReadOnlyList<GraphTask>> OnStartAsync(GraphSession session, CancellationToken ct);

    /// <summary>
    /// Called after each task completes (success or failure).
    /// <see cref="GraphSession.Snapshot"/> is already refreshed before this call.
    /// Returns the next tasks to schedule, or <see langword="null"/> to let the graph
    /// terminate naturally (no more tasks will be added).
    /// </summary>
    public abstract ValueTask<IReadOnlyList<GraphTask>?> OnTaskCompletedAsync(string taskId, GraphTaskStatus status, object? output, string? error, GraphSession session, CancellationToken ct);

    /// <summary>
    /// Called once before the scheduling loop starts when restoring from a persisted restore
    /// point. Allows orchestrators to rebuild their in-memory routing state (e.g. current slot,
    /// step counter) from the stored task metadata without replaying OnTaskCompletedAsync.
    /// The default implementation is a no-op.
    /// </summary>
    public virtual void NotifyTasksRestored(IReadOnlyList<GraphRestorePointTaskState> restoredStates) { }
}
