namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Declares the execution features for a single graph submitted to the <see cref="GraphHost"/>.
/// Features are per-graph — different graphs running concurrently inside the same host
/// can have different profiles.
/// </summary>
/// <remarks>
/// The <see cref="GraphHost"/> enforces <see cref="MaxParallelTasks"/> as a per-graph
/// cap on top of the host-wide <c>GlobalMaxConcurrentTasks</c> limit. Both limits apply:
/// a task must acquire a slot from the global pool AND respect the per-graph cap.
///
/// Use <see cref="InMemory"/> for graphs that do not need persistence or suspension
/// (e.g. LLMCompiler task batches). Use <see cref="Persistent"/> for workflow graphs
/// that must survive process restarts.
/// </remarks>
/// <param name="MaxParallelTasks">
/// Maximum number of tasks this graph may execute concurrently.
/// 0 means unbounded — only the host-wide pool limit applies.
/// </param>
/// <param name="Persistence">Controls when the <see cref="IGraphCheckpointPort"/> is called.</param>
/// <param name="allowWaiting">
/// When true the host honours <see cref="GraphTaskResult.Suspended"/> results and
/// pauses the graph pending external resumption. When false, a suspended task is
/// treated as a failure.
/// </param>
/// <param name="TaskTimeout">
/// Optional per-task execution timeout. The host cancels the task's token when elapsed.
/// Null means no timeout beyond the graph's own cancellation token.
/// </param>
/// <param name="MaxRetries">
/// Number of additional <see cref="GraphTask.ExecuteAsync"/> attempts after a failure
/// before the task is permanently failed. 0 means no retries.
/// </param>
/// <param name="Store">
/// The <see cref="IGraphCheckpointPort"/> used when <see cref="Persistence"/> is not
/// <see cref="GraphPersistenceMode.None"/>. Defaults to <see cref="NullGraphCheckpointPort.Instance"/>
/// when not supplied.
/// </param>
public sealed record GraphProfile(int MaxParallelTasks, GraphPersistenceMode Persistence, bool allowWaiting, TimeSpan? TaskTimeout, int MaxRetries, IGraphCheckpointPort? Store = null, GraphJournal? Journal = null)
{
    /// <summary>
    /// The effective store — falls back to <see cref="NullGraphCheckpointPort.Instance"/> when
    /// <see cref="Store"/> is null.
    /// </summary>
    public IGraphCheckpointPort EffectiveStore => Store ?? NullGraphCheckpointPort.Instance;

    /// <summary>
    /// Creates a profile for in-memory, non-persistent graph execution.
    /// Suitable for LLMCompiler task batches and any short-lived in-process graph.
    /// </summary>
    /// <param name="maxParallelTasks">Per-graph parallelism cap. 0 = unbounded.</param>
    /// <param name="allowWaiting">
    /// When true the host honours <see cref="GraphTaskResult.Suspended"/> results.
    /// Defaults to false — suspended tasks are treated as failures.
    /// </param>
    /// <param name="journal">
    /// Optional <see cref="GraphJournal"/> instance that the host will append engine-level
    /// events to (task completed/failed, graph submitted/finished).
    /// </param>
    public static GraphProfile InMemory(int maxParallelTasks = 0, bool allowWaiting = false, GraphJournal? journal = null) => new(MaxParallelTasks: maxParallelTasks, Persistence: GraphPersistenceMode.None, allowWaiting: allowWaiting, TaskTimeout: null, MaxRetries: 0, Journal: journal);

    /// <summary>
    /// Creates a profile for persistent graph execution with checkpoint-after-task semantics.
    /// Suitable for workflow graphs that must survive process restarts and support suspension.
    /// </summary>
    /// <param name="store">The store implementation to persist checkpoints.</param>
    /// <param name="maxParallelTasks">Per-graph parallelism cap. 0 = unbounded.</param>
    /// <param name="maxRetries">Retry count per failed task before permanent failure.</param>
    /// <param name="taskTimeout">Optional per-task timeout.</param>
    public static GraphProfile Persistent(IGraphCheckpointPort store, int maxParallelTasks = 0, int maxRetries = 0, TimeSpan? taskTimeout = null) =>
        new(MaxParallelTasks: maxParallelTasks, Persistence: GraphPersistenceMode.RestorePoint, allowWaiting: true, TaskTimeout: taskTimeout, MaxRetries: maxRetries, Store: store);
}
