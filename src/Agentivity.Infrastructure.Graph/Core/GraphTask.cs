namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The atomic unit of work in a <see cref="GraphRuntime"/>. Subclassed by callers —
/// never instantiated or examined by the graph engine beyond scheduling identity.
/// </summary>
/// <remarks>
/// Callers capture all required dependencies in the constructor:
/// <code>
/// sealed class PlanningTask(IAgentInfrastructure infra, string query) : GraphTask("planning", [])
/// {
///     public override async ValueTask&lt;GraphTaskResult&gt; ExecuteAsync(CancellationToken ct)
///     {
///         var result = await infra.BuildPlanAsync(query, ct);
///         return GraphTaskResult.Completed(result);
///     }
/// }
/// </code>
/// The graph engine calls <see cref="ExecuteAsync"/> once the task is activated.
/// The engine does not retry — retry policy is expressed in <see cref="GraphProfile.MaxRetries"/>
/// and enforced by the <see cref="GraphHost"/>.
///
/// Implement <see cref="ToDescriptor"/> to enable checkpoint persistence.
/// Tasks belonging to graphs with <see cref="GraphPersistenceMode.None"/> do not need
/// a meaningful descriptor implementation.
/// </remarks>
public abstract class GraphTask
{
    /// <summary>Initialises the task with its scheduling identity.</summary>
    /// <param name="id">Unique identifier within the graph. Must not be null or whitespace.</param>
    /// <param name="dependsOn">Ids of tasks that must complete before this task becomes ready. Pass an empty array for root tasks.</param>
    protected GraphTask(string id, string[] dependsOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(dependsOn);

        Id = id;
        DependsOn = dependsOn;
    }

    /// <summary>Unique identifier within the graph.</summary>
    public string Id { get; }

    /// <summary>Ids of tasks that must complete before this task becomes ready for activation.</summary>
    public IReadOnlyList<string> DependsOn { get; }

    /// <summary>
    /// Optional activation conditions that override the standard <see cref="DependsOn"/> evaluation.
    /// When set, all conditions must be satisfied for the task to activate.
    /// When null, the task uses the standard DependsOn (all predecessors must be Completed).
    /// </summary>
    public IReadOnlyList<GraphTaskActivationCondition>? ActivationConditions { get; init; }

    /// <summary>
    /// Optional join policy controlling how many predecessors must complete to activate this task.
    /// When null, defaults to All (all DependsOn must be Completed).
    /// </summary>
    public GraphTaskJoinPolicy? JoinPolicy { get; init; }

    /// <summary>
    /// Optional race group identifier. When multiple tasks share the same <see cref="RaceGroupId"/>,
    /// the first one to complete successfully causes all pending siblings to be skipped (Ignored).
    /// </summary>
    public string? RaceGroupId { get; init; }

    /// <summary>
    /// Optional type-specific metadata persisted in the graph restore point.
    /// Allows orchestrators to store the information needed to reconstruct this task
    /// from a restore point on resume (e.g. which agent slot this task represents),
    /// without replaying the full execution history.
    /// </summary>
    public IGraphTaskMetadata? Metadata { get; init; }

    /// <summary>
    /// Current runtime state for this task. Replaced by the <see cref="GraphRuntime"/>
    /// on every lifecycle transition. Read-only for all code outside the graph engine.
    /// </summary>
    public GraphTaskState State { get; internal set; } = new();

    /// <summary>
    /// The execution context shared across all tasks in this graph run.
    /// Injected by the host immediately before <see cref="ExecuteAsync"/> is called.
    /// </summary>
    private GraphExecutionContext? _context;

    /// <summary>
    /// Called by the host to bind the shared execution context before activation.
    /// </summary>
    internal void SetContext(GraphExecutionContext context) => _context = context;

    /// <summary>
    /// Returns the shared execution context cast to <typeparamref name="T"/>.
    /// Throws <see cref="InvalidOperationException"/> when the context has not been injected yet,
    /// and <see cref="InvalidCastException"/> when the context is not of the expected type.
    /// </summary>
    protected T Context<T>()
        where T : GraphExecutionContext => _context is not null ? (T)_context : throw new InvalidOperationException("Execution context has not been injected. Ensure the task is submitted through a GraphOrchestrator.");

    /// <summary>
    /// Executes the task work. Called exactly once by the <see cref="GraphHost"/> when the
    /// task is activated. Must not throw — return <see cref="GraphTaskResult.Failed"/> instead.
    /// </summary>
    public abstract ValueTask<GraphTaskResult> ExecuteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns the serialisable descriptor for this task, used by the <see cref="GraphHost"/>
    /// when persisting a checkpoint. The default implementation returns a minimal descriptor
    /// with no metadata — override in tasks belonging to persistent graphs.
    /// </summary>
    public virtual GraphTaskDescriptor ToDescriptor() => new(Id, [.. DependsOn], new Dictionary<string, object?>(StringComparer.Ordinal));
}
