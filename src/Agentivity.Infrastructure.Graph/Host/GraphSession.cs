namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The central object of a single graph execution. Created by <see cref="GraphHost"/>,
/// passed to <see cref="GraphOrchestrator"/> callbacks at each decision point.
/// Owns the orchestrator and exposes all graph operations scoped to this run.
/// The global <see cref="GraphHost"/> is never exposed through this object.
/// </summary>
public sealed class GraphSession
{
    // ── Identity & config ─────────────────────────────────────────────────────

    /// <summary>Unique identifier for this graph run.</summary>
    public GraphId GraphId { get; }

    /// <summary>Execution profile — parallelism, persistence, timeout settings.</summary>
    public GraphProfile Profile { get; }

    // ── Orchestrator ──────────────────────────────────────────────────────────

    /// <summary>The decision engine for this graph run.</summary>
    public GraphOrchestrator Orchestrator { get; }

    /// <summary>Shortcut to <see cref="GraphOrchestrator.Context"/>.</summary>
    public GraphExecutionContext Context => Orchestrator.Context;

    // ── Graph state ───────────────────────────────────────────────────────────

    /// <summary>
    /// Point-in-time snapshot of all task states.
    /// Refreshed by the host after every task completion, before the orchestrator is called.
    /// </summary>
    public GraphSnapshot Snapshot { get; internal set; } = default!;

    /// <summary>Returns the task snapshot with the given id, or null if not found.</summary>
    public GraphTaskSnapshot? FindTask(string taskId) => Snapshot.Tasks.FirstOrDefault(t => t.Id == taskId);

    /// <summary>Returns all task snapshots with the given runtime status.</summary>
    public IReadOnlyList<GraphTaskSnapshot> GetTasksByStatus(GraphTaskRuntimeStatus status) => Snapshot.Tasks.Where(t => t.State.RuntimeStatus == status).ToList();

    // ── Graph mutations ───────────────────────────────────────────────────────

    /// <summary>Schedules additional tasks into this graph execution.</summary>
    public ValueTask AddTasksAsync(IReadOnlyList<GraphTask> tasks, CancellationToken ct = default) => _host.AddTasksAsync(GraphId, tasks, ct);

    /// <summary>Removes a pending task from this graph execution.</summary>
    public ValueTask RemoveTaskAsync(string taskId, CancellationToken ct = default) => _host.RemoveTaskAsync(GraphId, taskId, ct);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Pauses scheduling of new tasks. Active tasks continue to completion.</summary>
    public ValueTask PauseAsync(CancellationToken ct = default) => _host.PauseAsync(GraphId, ct);

    /// <summary>Resumes a previously paused graph.</summary>
    public ValueTask ResumeAsync() => _host.ResumeAsync(GraphId);

    /// <summary>Cancels all active and future task executions for this graph.</summary>
    public ValueTask CancelAsync() => _host.CancelAsync(GraphId);

    /// <summary>
    /// Applies a previously saved restore point to the graph runtime.
    /// Tasks not yet added to the runtime are silently skipped.
    /// </summary>
    public void ApplyRestorePoint(GraphRestorePoint restorePoint) =>
        _host.ApplyRestorePoint(GraphId, restorePoint);

    // ── Internal ──────────────────────────────────────────────────────────────

    private readonly IGraphHost _host;

    internal GraphSession(GraphId graphId, GraphProfile profile, GraphOrchestrator orchestrator, IGraphHost host)
    {
        GraphId = graphId;
        Profile = profile;
        Orchestrator = orchestrator;
        _host = host;
    }
}
