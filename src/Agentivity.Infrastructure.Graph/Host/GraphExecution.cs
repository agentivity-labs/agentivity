namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Represents the live state of a single graph execution managed by a <see cref="IGraphHost"/>.
/// Encapsulates the <see cref="GraphRuntime"/>, lifecycle controls (pause/resume/cancel),
/// and the completion promise awaited by callers of <see cref="IGraphHost.AwaitCompletionAsync"/>.
/// </summary>
internal sealed class GraphExecution : IAsyncDisposable
{
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private readonly CancellationTokenSource _cts;

    /// <summary>
    /// Serialises mutations to <see cref="Runtime"/> (activation, completion, AddTasks)
    /// across the scheduling loop and callers such as <see cref="IGraphHost.AddTasksAsync"/>.
    /// </summary>
    public SemaphoreSlim RuntimeLock { get; } = new(1, 1);
    private readonly TaskCompletionSource<GraphExecutionResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile bool _isPaused;

    /// <summary>The unique identifier for this graph execution.</summary>
    public GraphId GraphId { get; }

    /// <summary>The runtime DAG scheduler for this execution.</summary>
    public GraphRuntime Runtime { get; }

    /// <summary>The profile declaring features for this graph.</summary>
    public GraphProfile Profile { get; }

    /// <summary>The orchestrator driving this graph, if submitted via the orchestrator overload.</summary>
    public GraphOrchestrator? Orchestrator { get; }

    /// <summary>The per-graph session exposed to the orchestrator.</summary>
    public GraphSession? Session { get; }

    /// <summary>
    /// Combined cancellation token: fires when the caller cancels or when
    /// <see cref="CancelAsync"/> is called.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Returns true when the graph has been paused via <see cref="PauseAsync"/>.</summary>
    public bool IsPaused => _isPaused;

    internal GraphExecution(GraphId graphId, GraphRuntime runtime, GraphProfile profile, CancellationToken linkedToken, GraphOrchestrator? orchestrator = null, GraphSession? session = null)
    {
        GraphId = graphId;
        Runtime = runtime;
        Profile = profile;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        CancellationToken = _cts.Token;
        Orchestrator = orchestrator;
        Session = session;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Blocks activation of new tasks without cancelling active ones.</summary>
    public async ValueTask PauseAsync()
    {
        _isPaused = true;
        await _pauseGate.WaitAsync().ConfigureAwait(false);
    }

    /// <summary>Allows task activation to proceed after a <see cref="PauseAsync"/>.</summary>
    public void Resume()
    {
        if (!_isPaused)
        {
            return;
        }

        _isPaused = false;
        if (_pauseGate.CurrentCount == 0)
        {
            _pauseGate.Release();
        }
    }

    /// <summary>Cancels all active and future task executions for this graph.</summary>
    public void Cancel() => _cts.Cancel();

    // ──────────────────────────────────────────────────────────────────────────────
    // Pause gate: acquired per scheduling cycle, released immediately after activation
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acquires the pause gate. The caller must release it immediately after reading
    /// ready tasks, so that a Pause that arrives while tasks are executing still
    /// blocks the NEXT scheduling cycle — not the current in-flight tasks.
    /// </summary>
    public async ValueTask AcquirePauseGateAsync(CancellationToken ct) => await _pauseGate.WaitAsync(ct).ConfigureAwait(false);

    /// <summary>Releases the pause gate acquired via <see cref="AcquirePauseGateAsync"/>.</summary>
    public void ReleasePauseGate()
    {
        if (_pauseGate.CurrentCount == 0)
        {
            _pauseGate.Release();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Completion promise
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Awaits the graph's terminal result.</summary>
    public Task<GraphExecutionResult> Completion => _tcs.Task;

    /// <summary>Signals successful completion to all awaiters.</summary>
    public void SetResult(GraphExecutionResult result) => _tcs.TrySetResult(result);

    /// <summary>Signals exceptional termination to all awaiters.</summary>
    public void SetException(Exception ex) => _tcs.TrySetException(ex);

    /// <summary>Signals cancellation to all awaiters.</summary>
    public void SetCancelled() => _tcs.TrySetCanceled();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _cts.Dispose();
        _pauseGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
