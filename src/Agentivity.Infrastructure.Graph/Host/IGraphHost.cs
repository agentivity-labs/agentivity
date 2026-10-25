namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The public contract for the graph execution kernel.
/// All graph clients (workflow, LLMCompiler, etc.) submit and manage graphs through this interface.
/// </summary>
public interface IGraphHost
{
    /// <summary>
    /// Submits a new graph for execution and starts scheduling immediately.
    /// </summary>
    /// <param name="tasks">The ordered task list for the graph. Must not be empty.</param>
    /// <param name="profile">Per-graph feature configuration.</param>
    /// <param name="cancellationToken">Token that cancels the entire graph execution.</param>
    /// <returns>The unique identifier assigned to this graph execution.</returns>
    ValueTask<GraphId> SubmitAsync(IReadOnlyList<GraphTask> tasks, GraphProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a new graph using a pre-allocated <paramref name="graphId"/>.
    /// Allows callers to wire the id into shared context before scheduling begins.
    /// </summary>
    ValueTask SubmitAsync(GraphId graphId, IReadOnlyList<GraphTask> tasks, GraphProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Awaits the terminal result of the graph identified by <paramref name="graphId"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No active graph with the given id exists.</exception>
    Task<GraphExecutionResult> AwaitCompletionAsync(GraphId graphId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses scheduling of new tasks for the graph. Active tasks continue to completion.
    /// </summary>
    ValueTask PauseAsync(GraphId graphId, CancellationToken cancellationToken = default);

    /// <summary>Resumes a previously paused graph.</summary>
    ValueTask ResumeAsync(GraphId graphId);

    /// <summary>
    /// Cancels all active and future task executions for the graph.
    /// <see cref="AwaitCompletionAsync"/> will throw <see cref="OperationCanceledException"/>.
    /// </summary>
    ValueTask CancelAsync(GraphId graphId);

    /// <summary>
    /// Dynamically appends <paramref name="tasks"/> to an active graph identified by
    /// <paramref name="graphId"/>. The scheduling loop will pick up newly-ready tasks
    /// on its next cycle without interrupting tasks that are already in progress.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No active graph with the given id exists.</exception>
    ValueTask AddTasksAsync(GraphId graphId, IReadOnlyList<GraphTask> tasks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a pending task from an active graph. Only tasks in the Pending state may be removed.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No active graph with the given id exists, or task not found.</exception>
    /// <exception cref="InvalidOperationException">The task is not in the Pending state.</exception>
    ValueTask RemoveTaskAsync(GraphId graphId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a new graph driven by a <paramref name="orchestrator"/>.
    /// The orchestrator's <see cref="GraphOrchestrator.OnStartAsync"/> is called to obtain
    /// the initial task set. Returns the <see cref="GraphSession"/> scoped to this run.
    /// </summary>
    ValueTask<GraphSession> SubmitAsync(GraphOrchestrator orchestrator, GraphProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a graph using a pre-allocated stable <paramref name="graphId"/> and a graph orchestrator.
    /// When <paramref name="restorePoint"/> is provided it is applied to the runtime immediately —
    /// before the scheduling loop starts — eliminating the race condition that would otherwise
    /// cause already-completed tasks to re-execute.
    /// </summary>
    ValueTask<GraphSession> SubmitAsync(GraphId graphId, GraphOrchestrator orchestrator, GraphProfile profile, GraphRestorePoint? restorePoint = null, IReadOnlyList<GraphTask>? additionalTasks = null, CancellationToken cancellationToken = default);

    /// <summary>Applies a restore point to an active graph execution, restoring task states.</summary>
    void ApplyRestorePoint(GraphId graphId, GraphRestorePoint restorePoint);

    /// <summary>
    /// Returns a point-in-time snapshot of the graph identified by <paramref name="graphId"/>.
    /// The snapshot is consistent with the graph runtime's last committed state but may lag
    /// behind by one scheduling cycle.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No active graph with the given id exists.</exception>
    GraphSnapshot GetSnapshot(GraphId graphId);

    /// <summary>
    /// Waits until no tasks in the graph are in progress (i.e. all running tasks have
    /// completed, failed, or suspended). Use this after <see cref="PauseAsync"/> to confirm
    /// that the pause has taken full effect before inspecting or persisting state.
    /// Returns immediately if the graph is already idle or no longer tracked.
    /// </summary>
    ValueTask WaitForIdleAsync(GraphId graphId, CancellationToken cancellationToken = default);
}
