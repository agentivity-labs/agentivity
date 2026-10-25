using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The graph execution kernel. Manages the full lifecycle of every submitted graph,
/// enforces a host-wide concurrency limit via a single <see cref="SemaphoreSlim"/>,
/// and delegates persistence to per-graph <see cref="IGraphCheckpointPort"/> instances declared
/// in each graph's <see cref="GraphProfile"/>.
/// </summary>
/// <remarks>
/// <para>
/// Thread safety: <see cref="GraphHost"/> is safe for concurrent use from multiple threads.
/// Internal synchronisation per graph is handled by a dedicated async lock on the
/// <see cref="GraphRuntime"/> to ensure single-threaded state mutations while allowing
/// fan-out task execution.
/// </para>
/// <para>
/// Scheduling loop (per graph):
/// <list type="number">
///   <item>Acquire pause gate.</item>
///   <item>Acquire global concurrency slots (one per ready task, up to per-graph cap).</item>
///   <item>Activate ready tasks via <see cref="GraphRuntime.TryActivateNext"/>.</item>
///   <item>Release pause gate.</item>
///   <item>Fan-out: each task runs on the thread pool (<c>Task.Run</c>), holding one global slot.</item>
///   <item>On completion: call <see cref="GraphRuntime.CompleteTask"/> / <see cref="GraphRuntime.FailTask"/> / <see cref="GraphRuntime.SuspendTask"/>, persist restore point if required, then re-enter the loop.</item>
/// </list>
/// </para>
/// <para>
/// Orchestrator callbacks (<see cref="GraphOrchestrator.OnTaskCompletedAsync"/>) and the
/// subsequent task injection happen INSIDE the runtime lock so the scheduling loop never
/// observes a spuriously terminal state between task completion and orchestrator-driven
/// follow-up tasks.
/// </para>
/// </remarks>
public sealed class GraphHost : IGraphHost, IAsyncDisposable
{
    private readonly SemaphoreSlim _globalSlots;
    private readonly ConcurrentDictionary<string, GraphExecution> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<GraphExecutionResult>> _completions = new(StringComparer.Ordinal);

    private readonly GraphHostOptions _options;

    public GraphHost(GraphHostOptions? options = null)
    {
        _options = options ?? new GraphHostOptions();
        _globalSlots = new SemaphoreSlim(_options.GlobalMaxConcurrentTasks, _options.GlobalMaxConcurrentTasks);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // IGraphHost
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ValueTask<GraphId> SubmitAsync(IReadOnlyList<GraphTask> tasks, GraphProfile profile, CancellationToken cancellationToken = default)
    {
        var graphId = GraphId.New();
        SubmitCore(graphId, tasks, profile, cancellationToken);
        return new ValueTask<GraphId>(graphId);
    }

    /// <inheritdoc/>
    public ValueTask SubmitAsync(GraphId graphId, IReadOnlyList<GraphTask> tasks, GraphProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graphId);
        SubmitCore(graphId, tasks, profile, cancellationToken);
        return ValueTask.CompletedTask;
    }

    private void SubmitCore(GraphId graphId, IReadOnlyList<GraphTask> tasks, GraphProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(profile);

        var runtime = new GraphRuntime(graphId.ToString());
        runtime.ReplaceTasks(tasks);

        var execution = new GraphExecution(graphId, runtime, profile, cancellationToken);
        _executions[graphId.ToString()] = execution;
        _completions[graphId.ToString()] = execution.Completion;

        profile.Journal?.Append(new GraphSubmittedItem(DateTimeOffset.UtcNow, graphId.ToString()));

        _ = RunSchedulingLoopAsync(execution);
    }

    /// <inheritdoc/>
    public async Task<GraphExecutionResult> AwaitCompletionAsync(GraphId graphId, CancellationToken cancellationToken = default)
    {
        if (!_completions.TryGetValue(graphId.ToString(), out var completionTask))
            throw new KeyNotFoundException($"No active graph execution found for id '{graphId}'.");

        // Wire cancellation: if the caller cancels, also cancel the execution if still live.
        if (cancellationToken.CanBeCanceled)
        {
            using var reg = cancellationToken.UnsafeRegister(
                static state =>
                {
                    var (host, id) = ((GraphHost, GraphId))state!;
                    if (host._executions.TryGetValue(id.ToString(), out var e))
                        e.Cancel();
                },
                (this, graphId)
            );

            var result = await completionTask.ConfigureAwait(false);
            _completions.TryRemove(graphId.ToString(), out _);
            return result;
        }

        var res = await completionTask.ConfigureAwait(false);
        _completions.TryRemove(graphId.ToString(), out _);
        return res;
    }

    /// <inheritdoc/>
    public async ValueTask PauseAsync(GraphId graphId, CancellationToken cancellationToken = default)
    {
        // Graph already completed/cancelled — nothing to pause.
        if (!_executions.TryGetValue(graphId.ToString(), out var execution))
            return;
        await execution.PauseAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask ResumeAsync(GraphId graphId)
    {
        var execution = RequireExecution(graphId);
        execution.Resume();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask CancelAsync(GraphId graphId)
    {
        var execution = RequireExecution(graphId);
        execution.Cancel();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask RemoveTaskAsync(GraphId graphId, string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var execution = RequireExecution(graphId);
        await execution.RuntimeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            execution.Runtime.RemoveTask(taskId);
        }
        finally
        {
            execution.RuntimeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<GraphSession> SubmitAsync(GraphOrchestrator orchestrator, GraphProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(profile);

        var graphId = GraphId.New();
        var runtime = new GraphRuntime(graphId.ToString());
        var execution = new GraphExecution(graphId, runtime, profile, cancellationToken, orchestrator: null, session: null);

        var session = new GraphSession(graphId, profile, orchestrator, this);
        session.Snapshot = runtime.BuildSnapshot();

        // Reconstruct with session attached (session ref needed before scheduling loop).
        var executionWithSession = new GraphExecution(graphId, runtime, profile, cancellationToken, orchestrator, session);
        _executions[graphId.ToString()] = executionWithSession;
        _completions[graphId.ToString()] = executionWithSession.Completion;

        profile.Journal?.Append(new GraphSubmittedItem(DateTimeOffset.UtcNow, graphId.ToString()));

        var initialTasks = await orchestrator.OnStartAsync(session, cancellationToken).ConfigureAwait(false);
        if (initialTasks?.Count > 0)
        {
            runtime.ReplaceTasks(initialTasks, incrementRevision: false);
            session.Snapshot = runtime.BuildSnapshot();
        }

        _ = RunSchedulingLoopAsync(executionWithSession);

        return session;
    }

    /// <inheritdoc/>
    public async ValueTask<GraphSession> SubmitAsync(GraphId graphId, GraphOrchestrator orchestrator, GraphProfile profile, GraphRestorePoint? restorePoint = null, IReadOnlyList<GraphTask>? additionalTasks = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graphId);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(profile);

        var runtime = new GraphRuntime(graphId.ToString());
        var session = new GraphSession(graphId, profile, orchestrator, this);
        session.Snapshot = runtime.BuildSnapshot();

        var execution = new GraphExecution(graphId, runtime, profile, cancellationToken, orchestrator, session);
        _executions[graphId.ToString()] = execution;
        _completions[graphId.ToString()] = execution.Completion;

        profile.Journal?.Append(new GraphSubmittedItem(DateTimeOffset.UtcNow, graphId.ToString()));

        var initialTasks = await orchestrator.OnStartAsync(session, cancellationToken).ConfigureAwait(false);
        if (initialTasks?.Count > 0)
        {
            runtime.ReplaceTasks(initialTasks, incrementRevision: false);
            session.Snapshot = runtime.BuildSnapshot();
        }

        // On resume: add tasks reconstructed from typed metadata (definition), then apply
        // saved statuses (state). Separates definition from state without replay.
        if (restorePoint is not null)
        {
            if (additionalTasks?.Count > 0)
                runtime.AddTasks(additionalTasks);
            runtime.RestoreFrom(restorePoint, ignoreUnknownTasks: true);
            session.Snapshot = runtime.BuildSnapshot();
        }

        _ = RunSchedulingLoopAsync(execution);

        return session;
    }

    /// <inheritdoc/>
    public async ValueTask AddTasksAsync(GraphId graphId, IReadOnlyList<GraphTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var execution = RequireExecution(graphId);
        // AddTasks mutates _orderedStates which is also read by the scheduling loop
        // under RuntimeLock — acquire the same lock to prevent concurrent modification.
        await execution.RuntimeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            execution.Runtime.AddTasks(tasks, incrementRevision: false);
        }
        finally
        {
            execution.RuntimeLock.Release();
        }
    }

    /// <inheritdoc/>
    internal GraphExecution? TryGetExecution(GraphId graphId) => _executions.TryGetValue(graphId.ToString(), out var e) ? e : null;

    /// <inheritdoc/>
    public GraphSnapshot GetSnapshot(GraphId graphId)
    {
        var execution = RequireExecution(graphId);
        return execution.Runtime.BuildSnapshot();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Scheduling loop
    // ──────────────────────────────────────────────────────────────────────────────

    private async Task RunSchedulingLoopAsync(GraphExecution execution)
    {
        var ct = execution.CancellationToken;
        var syncLock = execution.RuntimeLock;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Acquire the per-execution pause gate — blocks while graph is paused.
                await execution.AcquirePauseGateAsync(ct).ConfigureAwait(false);

                GraphActivation? activation;
                await syncLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var maxParallel = execution.Profile.MaxParallelTasks > 0 ? execution.Profile.MaxParallelTasks : (int?)null;

                    activation = execution.Runtime.TryActivateNext(maxParallel);
                }
                finally
                {
                    syncLock.Release();
                    execution.ReleasePauseGate();
                }

                if (activation is null)
                {
                    // Check terminal condition.
                    await syncLock.WaitAsync(ct).ConfigureAwait(false);
                    GraphStatusSummary summary;
                    try
                    {
                        summary = execution.Runtime.BuildStatusSummary();
                    }
                    finally
                    {
                        syncLock.Release();
                    }

                    if (summary.IsTerminal || summary.TotalCount == 0)
                    {
                        await FinaliseExecutionAsync(execution, summary, syncLock).ConfigureAwait(false);
                        return;
                    }

                    // Active tasks are still running — wait for one to complete.
                    await Task.Yield();
                    continue;
                }

                // Fan-out activated tasks.
                foreach (var task in activation.ActivatedTasks)
                {
                    _ = RunTaskAsync(task, execution, syncLock, ct);
                }
            }
            // Loop exited via while condition (ct cancelled without throwing) — signal waiters.
            execution.SetCancelled();
            CleanupExecution(execution);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            execution.SetCancelled();
            CleanupExecution(execution);
        }
        catch (Exception ex)
        {
            execution.SetException(ex);
            CleanupExecution(execution);
        }
    }

    private async Task RunTaskAsync(GraphTask task, GraphExecution execution, SemaphoreSlim syncLock, CancellationToken ct)
    {
        // Acquire one global slot — blocks when host is at capacity.
        await _globalSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Inject the shared execution context so the task can access it via Context<T>().
            if (execution.Orchestrator?.Context is { } ctx)
                task.SetContext(ctx);

            GraphTaskResult result;
            try
            {
                result = await ExecuteWithTimeoutAsync(task, execution.Profile, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = GraphTaskResult.Failed("cancelled");
            }
            catch (Exception ex) when (ex is IGraphSignalException)
            {
                // Signal exceptions (e.g. workflow suspension) bypass the normal
                // Failed path and terminate the whole graph execution immediately.
                // Persist the current runtime state before signalling so that callers
                // can read the restore point (e.g. task still InProgress) after suspension.
                if (execution.Profile.Persistence != GraphPersistenceMode.None)
                {
                    var restorePoint = execution.Runtime.BuildRestorePoint();
                    await execution.Profile.EffectiveStore.SaveAsync(execution.GraphId, restorePoint, CancellationToken.None).ConfigureAwait(false);
                }

                execution.SetException(ex);
                CleanupExecution(execution);
                return;
            }
            catch (Exception ex)
            {
                // Unexpected exception from a graph task — propagate immediately so the
                // caller gets the original exception rather than a degraded error string.
                execution.SetException(ex);
                CleanupExecution(execution);
                return;
            }

            // Apply result with retry policy.
            result = await ApplyRetryPolicyAsync(task, result, execution.Profile, ct).ConfigureAwait(false);

            // Mutate runtime state + orchestrator injection atomically under lock.
            // CompleteTask and AddTasks (from OnTaskCompletedAsync) must be in the same
            // critical section so the scheduling loop never observes a spuriously terminal
            // state between the two operations.
            GraphCompletion completion;
            await syncLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                completion = result.Status switch
                {
                    GraphTaskStatus.Completed => execution.Runtime.CompleteTask(task.Id, result.Output),
                    GraphTaskStatus.Failed => execution.Runtime.FailTask(task.Id, result.Error),
                    GraphTaskStatus.Suspended when execution.Profile.allowWaiting => execution.Runtime.SuspendTask(task.Id, result.SuspensionReason, result.WaitingForToken),
                    GraphTaskStatus.Suspended => execution.Runtime.FailTask(task.Id, $"suspension_not_allowed: {result.SuspensionReason}"),
                    _ => execution.Runtime.FailTask(task.Id, "unknown_status"),
                };

                if (result.Status == GraphTaskStatus.Completed && task.RaceGroupId is { } raceGroupId)
                {
                    execution.Runtime.CancelRaceGroupSiblings(task.Id, raceGroupId);
                }

                // Emit history event for the task outcome.
                if (execution.Profile.Journal is { } journal)
                {
                    if (result.Status == GraphTaskStatus.Completed)
                        journal.Append(new GraphTaskCompletedItem(DateTimeOffset.UtcNow, execution.GraphId.ToString(), task.Id, result.Output));
                    else
                        journal.Append(new GraphTaskFailedItem(DateTimeOffset.UtcNow, execution.GraphId.ToString(), task.Id, result.Error));
                }

                // Notify the orchestrator and inject next tasks before releasing the lock
                // so the scheduling loop never sees a spuriously terminal state.
                if (execution.Orchestrator is { } orch && execution.Session is { } sess)
                {
                    sess.Snapshot = completion.Snapshot;
                    var nextTasks = await orch.OnTaskCompletedAsync(task.Id, result.Status, result.Status == GraphTaskStatus.Completed ? result.Output : null, result.Status == GraphTaskStatus.Failed ? result.Error : null, sess, ct).ConfigureAwait(false);

                    if (nextTasks?.Count > 0)
                    {
                        execution.Runtime.AddTasks(nextTasks, incrementRevision: false);
                        sess.Snapshot = execution.Runtime.BuildSnapshot();
                    }
                }
            }
            finally
            {
                syncLock.Release();
            }

            // Persist if required (outside lock).
            if (execution.Profile.Persistence != GraphPersistenceMode.None)
            {
                var restorePoint = execution.Runtime.BuildRestorePoint();
                await execution.Profile.EffectiveStore.SaveAsync(execution.GraphId, restorePoint, ct).ConfigureAwait(false);
            }

            _ = completion; // snapshot is observable via TryGetExecution
        }
        finally
        {
            _globalSlots.Release();
        }
    }

    private static async ValueTask<GraphTaskResult> ExecuteWithTimeoutAsync(GraphTask task, GraphProfile profile, CancellationToken ct)
    {
        if (profile.TaskTimeout is null)
        {
            return await task.ExecuteAsync(ct).ConfigureAwait(false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(profile.TaskTimeout.Value);

        return await task.ExecuteAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private static async ValueTask<GraphTaskResult> ApplyRetryPolicyAsync(GraphTask task, GraphTaskResult result, GraphProfile profile, CancellationToken ct)
    {
        if (result.Status != GraphTaskStatus.Failed || profile.MaxRetries <= 0)
        {
            return result;
        }

        for (var attempt = 0; attempt < profile.MaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                result = await task.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return GraphTaskResult.Failed("cancelled_during_retry");
            }

            if (result.Status != GraphTaskStatus.Failed)
            {
                return result;
            }
        }

        return result;
    }

    private async Task FinaliseExecutionAsync(GraphExecution execution, GraphStatusSummary summary, SemaphoreSlim syncLock)
    {
        await syncLock.WaitAsync().ConfigureAwait(false);
        GraphSnapshot finalSnapshot;
        try
        {
            finalSnapshot = execution.Runtime.BuildSnapshot();
        }
        finally
        {
            syncLock.Release();
        }

        var outputs = finalSnapshot.Tasks.Where(t => t.State.Artifact is not null || t.State.Output is not null).ToDictionary(t => t.Id, t => (object?)(t.State.Artifact ?? t.State.Output), StringComparer.Ordinal);

        var overallStatus =
            summary.FailedCount > 0 ? GraphTaskStatus.Failed
            : summary.SuspendedCount > 0 ? GraphTaskStatus.Suspended
            : GraphTaskStatus.Completed;

        var WaitingForToken = overallStatus == GraphTaskStatus.Suspended ? finalSnapshot.Tasks.FirstOrDefault(t => t.State.RuntimeStatus == GraphTaskRuntimeStatus.Suspended)?.State.WaitingForToken : null;

        var executionResult = new GraphExecutionResult(execution.GraphId, finalSnapshot, overallStatus, DateTimeOffset.UtcNow, outputs, WaitingForToken);

        // Final restore point on full completion.
        if (execution.Profile.Persistence == GraphPersistenceMode.Full)
        {
            var restorePoint = execution.Runtime.BuildRestorePoint();
            await execution.Profile.EffectiveStore.SaveAsync(execution.GraphId, restorePoint, CancellationToken.None).ConfigureAwait(false);
        }

        execution.Profile.Journal?.Append(new GraphFinishedItem(DateTimeOffset.UtcNow, execution.GraphId.ToString(), overallStatus));

        execution.SetResult(executionResult);
        CleanupExecution(execution);
    }

    private void CleanupExecution(GraphExecution execution)
    {
        _executions.TryRemove(execution.GraphId.ToString(), out _);
        // _completions is intentionally NOT cleaned here; entries are kept until
        // AwaitCompletionAsync observes them to avoid a race between scheduling-loop
        // completion and the caller reaching AwaitCompletionAsync.
        _ = execution.DisposeAsync();
    }

    private GraphExecution RequireExecution(GraphId graphId) => _executions.TryGetValue(graphId.ToString(), out var e) ? e : throw new KeyNotFoundException($"No active graph execution found for id '{graphId}'.");

    /// <inheritdoc/>
    public void ApplyRestorePoint(GraphId graphId, GraphRestorePoint restorePoint)
    {
        var execution = RequireExecution(graphId);
        execution.Runtime.RestoreFrom(restorePoint, ignoreUnknownTasks: true);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // WaitForIdleAsync
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask WaitForIdleAsync(GraphId graphId, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Graph no longer tracked (completed/cancelled) — already idle.
            if (!_executions.TryGetValue(graphId.ToString(), out var execution))
                return;

            var snapshot = execution.Runtime.BuildSnapshot();
            if (!snapshot.Tasks.Any(t => t.State.RuntimeStatus == GraphTaskRuntimeStatus.InProgress))
                return;

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // IAsyncDisposable
    // ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var execution in _executions.Values)
        {
            execution.Cancel();
        }

        // Give in-flight tasks a moment to react to cancellation.
        await Task.Delay(100).ConfigureAwait(false);

        foreach (var execution in _executions.Values)
        {
            await execution.DisposeAsync().ConfigureAwait(false);
        }

        _executions.Clear();
        _completions.Clear();
        _globalSlots.Dispose();
    }
}
