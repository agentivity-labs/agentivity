namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// A pure in-process DAG scheduler. Tracks task lifecycle state, computes ready/blocked
/// status, propagates ignore signals on failure, and produces serialisable snapshots.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GraphRuntime"/> contains no I/O, no threading, and no persistence logic.
/// All concurrency, timeout enforcement, retry policy, and store writes are the
/// responsibility of the caller (i.e. the <c>GraphHost</c>).
/// </para>
/// <para>
/// A runtime is created once per graph execution. Tasks are registered via
/// <see cref="ReplaceTasks"/>, which also increments the revision counter.
/// Callers drive the execution loop with:
/// <list type="number">
///   <item><see cref="TryActivateNext"/> — move ready tasks to <see cref="GraphTaskRuntimeStatus.InProgress"/>.</item>
///   <item><see cref="CompleteTask"/> / <see cref="FailTask"/> / <see cref="SuspendTask"/> — advance lifecycle state.</item>
///   <item><see cref="BuildSnapshot"/> — observe current state at any time.</item>
///   <item><see cref="BuildStatusSummary"/> — quick terminal / health check.</item>
/// </list>
/// </para>
/// <para>
/// Thread safety: <see cref="GraphRuntime"/> is NOT thread-safe. Concurrent calls from
/// the host must be serialised (e.g. using an async lock or channel).
/// </para>
/// </remarks>
public sealed class GraphRuntime
{
    private readonly Dictionary<string, GraphTask> _tasksById = new(StringComparer.Ordinal);

    private readonly List<GraphTask> _orderedTasks = [];
    private readonly HashSet<string> _activeTaskIds = new(StringComparer.Ordinal);

    /// <summary>Unique identifier for this graph.</summary>
    public string GraphId { get; }

    /// <summary>
    /// Monotonically increasing counter incremented each time <see cref="ReplaceTasks"/> is called
    /// with <c>incrementRevision: true</c> (the default).
    /// </summary>
    public int RevisionCount { get; private set; }

    /// <summary>Returns true when at least one task is currently in progress.</summary>
    public bool HasActiveTasks => _activeTaskIds.Count > 0;

    /// <summary>Returns the ids of all tasks currently in progress.</summary>
    public IReadOnlyCollection<string> ActiveTaskIds => _activeTaskIds;

    /// <summary>Initialises a new runtime for the graph identified by <paramref name="graphId"/>.</summary>
    public GraphRuntime(string graphId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        GraphId = graphId;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Task registration
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the current task set with the provided tasks. Clears all in-progress state.
    /// </summary>
    /// <param name="tasks">Ordered list of tasks. Order determines snapshot ordering.</param>
    /// <param name="incrementRevision">When true (default), <see cref="RevisionCount"/> is incremented.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tasks"/> is null.</exception>
    /// <exception cref="ArgumentException">Duplicate task ids found in <paramref name="tasks"/>.</exception>
    public void ReplaceTasks(IReadOnlyList<GraphTask> tasks, bool incrementRevision = true)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        _tasksById.Clear();
        _orderedTasks.Clear();
        _activeTaskIds.Clear();

        foreach (var task in tasks)
        {
            task.State = new GraphTaskState();
            if (!_tasksById.TryAdd(task.Id, task))
                throw new ArgumentException($"Duplicate task id '{task.Id}'.", nameof(tasks));
            _orderedTasks.Add(task);
        }

        if (incrementRevision)
            RevisionCount++;
    }

    /// <summary>
    /// Appends additional tasks to the existing task set without clearing current state.
    /// Tasks already tracked (same id) are silently skipped.
    /// </summary>
    /// <param name="tasks">Tasks to append. Must not contain ids that conflict with existing tasks (ids not already present are added).</param>
    /// <param name="incrementRevision">When true (default), <see cref="RevisionCount"/> is incremented.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tasks"/> is null.</exception>
    /// <exception cref="ArgumentException">Duplicate ids within <paramref name="tasks"/> itself.</exception>
    public void AddTasks(IReadOnlyList<GraphTask> tasks, bool incrementRevision = true)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (!seenInBatch.Add(task.Id))
                throw new ArgumentException($"Duplicate task id '{task.Id}' in added tasks.", nameof(tasks));

            if (_tasksById.TryAdd(task.Id, task))
            {
                task.State = new GraphTaskState();
                _orderedTasks.Add(task);
            }
        }

        if (incrementRevision)
            RevisionCount++;
    }

    /// <summary>
    /// Removes a pending task from the graph. Only tasks in the
    /// <see cref="GraphTaskRuntimeStatus.Pending"/> state may be removed;
    /// attempting to remove an active, completed, or failed task throws.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No task with <paramref name="taskId"/> exists.</exception>
    /// <exception cref="InvalidOperationException">The task is not in the Pending state.</exception>
    public void RemoveTask(string taskId)
    {
        if (!_tasksById.TryGetValue(taskId, out var task))
            throw new KeyNotFoundException($"Task '{taskId}' not found in the graph.");

        if (task.State.RuntimeStatus != GraphTaskRuntimeStatus.Pending)
            throw new InvalidOperationException($"Cannot remove task '{taskId}': expected Pending, got {task.State.RuntimeStatus}.");

        _tasksById.Remove(taskId);
        _orderedTasks.Remove(task);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Activation
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Activates all ready tasks (up to <paramref name="maxParallelTasks"/> if specified),
    /// transitioning them from <see cref="GraphTaskRuntimeStatus.Pending"/> to
    /// <see cref="GraphTaskRuntimeStatus.InProgress"/>.
    /// </summary>
    /// <param name="maxParallelTasks">
    /// Maximum number of tasks to activate in this call, or null for no cap beyond the
    /// number of ready tasks. Tasks already in progress count toward this limit.
    /// </param>
    /// <returns>
    /// A <see cref="GraphActivation"/> with the activated tasks, or null when no tasks are
    /// ready or the active cap is already reached.
    /// </returns>
    public GraphActivation? TryActivateNext(int? maxParallelTasks = null)
    {
        var availableSlots = maxParallelTasks.HasValue && maxParallelTasks.Value > 0 ? maxParallelTasks.Value - _activeTaskIds.Count : int.MaxValue;

        if (availableSlots <= 0)
            return null;

        PropagateConditionalSkip();

        var readyTasks = _orderedTasks.Where(IsReady).Take(availableSlots).ToList();

        if (readyTasks.Count == 0)
            return null;

        foreach (var task in readyTasks)
        {
            task.State = task.State with { RuntimeStatus = GraphTaskRuntimeStatus.InProgress };
            _activeTaskIds.Add(task.Id);
        }

        var snapshot = BuildSnapshot();
        return new GraphActivation(readyTasks, snapshot);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Completion transitions
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks the task identified by <paramref name="taskId"/> as
    /// <see cref="GraphTaskRuntimeStatus.Completed"/> with the given output.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The task is not currently in progress.
    /// </exception>
    public GraphCompletion CompleteTask(string taskId, object? output = null)
    {
        var task = RequireActiveTask(taskId);
        task.State = output is GraphTaskArtifact artifact ? task.State with { RuntimeStatus = GraphTaskRuntimeStatus.Completed, Artifact = artifact } : task.State with { RuntimeStatus = GraphTaskRuntimeStatus.Completed, Output = output };
        _activeTaskIds.Remove(taskId);
        return new GraphCompletion([taskId], BuildSnapshot());
    }

    /// <summary>
    /// Marks the task identified by <paramref name="taskId"/> as
    /// <see cref="GraphTaskRuntimeStatus.Failed"/> and propagates
    /// <see cref="GraphTaskRuntimeStatus.Ignored"/> to all direct and transitive dependents.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The task is not currently in progress.
    /// </exception>
    public GraphCompletion FailTask(string taskId, string? error = null)
    {
        var task = RequireActiveTask(taskId);
        task.State = task.State with { RuntimeStatus = GraphTaskRuntimeStatus.Failed, Error = error };
        _activeTaskIds.Remove(taskId);
        PropagateIgnored([taskId], failedDependency: true);
        return new GraphCompletion([taskId], BuildSnapshot());
    }

    /// <summary>
    /// Marks the task identified by <paramref name="taskId"/> as
    /// <see cref="GraphTaskRuntimeStatus.Suspended"/>.
    /// The graph is effectively paused until the task is resumed or failed externally.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The task is not currently in progress.
    /// </exception>
    public GraphCompletion SuspendTask(string taskId, string? reason = null, string? token = null)
    {
        var task = RequireActiveTask(taskId);
        task.State = task.State with { RuntimeStatus = GraphTaskRuntimeStatus.Suspended, SuspensionReason = reason, WaitingForToken = token };
        _activeTaskIds.Remove(taskId);
        return new GraphCompletion([taskId], BuildSnapshot());
    }

    /// <summary>
    /// Re-activates a previously suspended task, transitioning it back to
    /// <see cref="GraphTaskRuntimeStatus.InProgress"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The task is not in the <see cref="GraphTaskRuntimeStatus.Suspended"/> state.
    /// </exception>
    public void ResumeTask(string taskId)
    {
        if (!_tasksById.TryGetValue(taskId, out var task) || task.State.RuntimeStatus != GraphTaskRuntimeStatus.Suspended)
        {
            throw new InvalidOperationException($"Cannot resume task '{taskId}': expected Suspended, got {_tasksById.GetValueOrDefault(taskId)?.State.RuntimeStatus.ToString() ?? "unknown"}.");
        }

        task.State = task.State with { RuntimeStatus = GraphTaskRuntimeStatus.InProgress, SuspensionReason = null };
        _activeTaskIds.Add(taskId);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Snapshot / observability
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a serialisable, immutable snapshot of the current graph state.
    /// Captures every task's runtime status, scheduling status, output, error, and blocking reason.
    /// </summary>
    public GraphSnapshot BuildSnapshot() => new(GraphId, RevisionCount, [.. _orderedTasks.Select(t => new GraphTaskSnapshot(t.Id, t.DependsOn, t.State, GetSchedulingStatus(t), GetBlockingReason(t)))]);

    /// <summary>
    /// Returns aggregate counts by scheduling status plus terminal/success indicators.
    /// Suspended tasks are tracked separately from truly-active (in-progress) tasks so
    /// that <see cref="GraphStatusSummary.IsTerminal"/> can correctly detect the end of
    /// a scheduling cycle even when suspension is the graph's final state.
    /// </summary>
    public GraphStatusSummary BuildStatusSummary()
    {
        var counts = _orderedTasks.GroupBy(GetSchedulingStatus).ToDictionary(g => g.Key, g => g.Count());

        var suspendedCount = _orderedTasks.Count(t => t.State.RuntimeStatus == GraphTaskRuntimeStatus.Suspended);

        return new GraphStatusSummary(
            TotalCount: _orderedTasks.Count,
            CompletedCount: counts.GetValueOrDefault(GraphTaskSchedulingStatus.Completed),
            ActiveCount: counts.GetValueOrDefault(GraphTaskSchedulingStatus.Active) - suspendedCount,
            SuspendedCount: suspendedCount,
            ReadyCount: counts.GetValueOrDefault(GraphTaskSchedulingStatus.Ready),
            BlockedCount: counts.GetValueOrDefault(GraphTaskSchedulingStatus.Blocked),
            FailedCount: counts.GetValueOrDefault(GraphTaskSchedulingStatus.Failed),
            IgnoredCount: counts.GetValueOrDefault(GraphTaskSchedulingStatus.Ignored)
        );
    }

    /// <summary>
    /// Produces a restore point containing only tasks that have moved past
    /// <see cref="GraphTaskRuntimeStatus.Pending"/>.
    /// </summary>
    public GraphRestorePoint BuildRestorePoint()
    {
        var taskStates = _orderedTasks.Where(t => t.State.RuntimeStatus != GraphTaskRuntimeStatus.Pending).Select(t => new GraphRestorePointTaskState(t.Id, t.State.RuntimeStatus, t.State.Artifact, t.State.Output, t.State.Error, t.State.SuspensionReason, t.State.WaitingForToken, t.Metadata)).ToList();

        return new GraphRestorePoint(GraphId, RevisionCount, taskStates);
    }

    /// <summary>
    /// Restores task states from a previously produced <see cref="GraphRestorePoint"/>.
    /// The current task set (registered via <see cref="ReplaceTasks"/>) is used and
    /// must already contain all restore point task ids — the orchestrator is responsible
    /// for seeding the correct tasks before calling this method.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A restore point task id is not found in the current task set.
    /// </exception>
    public void RestoreFrom(GraphRestorePoint restorePoint, bool ignoreUnknownTasks = false)
    {
        ArgumentNullException.ThrowIfNull(restorePoint);

        _activeTaskIds.Clear();
        RevisionCount = restorePoint.RevisionCount;

        foreach (var entry in restorePoint.TaskStates)
        {
            if (!_tasksById.TryGetValue(entry.TaskId, out var task))
            {
                if (!ignoreUnknownTasks)
                    throw new InvalidOperationException($"RestorePoint references unknown task '{entry.TaskId}'.");
                continue;
            }

            // Suspended tasks are re-queued as Pending so the scheduling loop re-executes them.
            // Their HIL proxy (HilWaitProxy) will detect the stored interaction response
            // via TryResumeAsync and inject it without calling the LLM again.
            var effectiveStatus = entry.RuntimeStatus == GraphTaskRuntimeStatus.Suspended
                ? GraphTaskRuntimeStatus.Pending
                : entry.RuntimeStatus;

            task.State = new GraphTaskState
            {
                RuntimeStatus = effectiveStatus,
                Artifact = entry.Artifact,
                Output = effectiveStatus == GraphTaskRuntimeStatus.Pending ? null : entry.Output,
                Error = entry.Error,
                SuspensionReason = effectiveStatus == GraphTaskRuntimeStatus.Pending ? null : entry.SuspensionReason,
                WaitingForToken = effectiveStatus == GraphTaskRuntimeStatus.Pending ? null : entry.WaitingForToken,
            };

            if (effectiveStatus == GraphTaskRuntimeStatus.InProgress)
                _activeTaskIds.Add(entry.TaskId);
        }
    }

    /// <summary>
    /// Returns the blocking reason for a specific task by id, or null when the task
    /// is not blocked, failed, or ignored.
    /// </summary>
    public string? GetBlockingReason(string taskId) => _tasksById.TryGetValue(taskId, out var task) ? GetBlockingReason(task) : null;

    // ──────────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────────

    private bool IsReady(GraphTask task)
    {
        if (task.State.RuntimeStatus != GraphTaskRuntimeStatus.Pending)
            return false;

        if (task.ActivationConditions is { Count: > 0 } conditions)
            return AreActivationConditionsMet(conditions);

        if (task.JoinPolicy is not null)
            return IsReadyWithJoinPolicy(task);

        // Default: all DependsOn must be Completed
        return task.DependsOn.All(dep => _tasksById.TryGetValue(dep, out var d) && d.State.RuntimeStatus == GraphTaskRuntimeStatus.Completed);
    }

    private bool AreActivationConditionsMet(IReadOnlyList<GraphTaskActivationCondition> conditions)
    {
        foreach (var condition in conditions)
        {
            if (!_tasksById.TryGetValue(condition.DependencyTaskId, out var dep))
                return false;

            var met = condition switch
            {
                GraphTaskActivationCondition.Always => dep.State.RuntimeStatus == GraphTaskRuntimeStatus.Completed,
                GraphTaskActivationCondition.WhenOutput whenOutput => dep.State.RuntimeStatus == GraphTaskRuntimeStatus.Completed && whenOutput.Predicate(dep.State.Output ?? dep.State.Artifact),
                GraphTaskActivationCondition.WhenIgnored => dep.State.RuntimeStatus is GraphTaskRuntimeStatus.Ignored,
                _ => false,
            };

            if (!met)
                return false;
        }
        return true;
    }

    private bool IsReadyWithJoinPolicy(GraphTask task)
    {
        var completedCount = task.DependsOn.Count(dep => _tasksById.TryGetValue(dep, out var d) && d.State.RuntimeStatus == GraphTaskRuntimeStatus.Completed);

        return task.JoinPolicy switch
        {
            GraphTaskJoinPolicy.All => completedCount == task.DependsOn.Count,
            GraphTaskJoinPolicy.Any => completedCount >= 1,
            GraphTaskJoinPolicy.AtLeast atLeast => completedCount >= atLeast.N,
            _ => completedCount == task.DependsOn.Count,
        };
    }

    private IEnumerable<string> GetPendingDependencies(GraphTask task)
    {
        if (task.State.RuntimeStatus != GraphTaskRuntimeStatus.Pending)
            yield break;

        // Tasks with activation conditions are blocked until the conditions are met or skipped
        if (task.ActivationConditions is { Count: > 0 } conditions)
        {
            foreach (var condition in conditions)
            {
                if (!_tasksById.TryGetValue(condition.DependencyTaskId, out var dep))
                    yield return condition.DependencyTaskId;
                else
                {
                    var conditionMet = condition switch
                    {
                        GraphTaskActivationCondition.Always => dep.State.RuntimeStatus == GraphTaskRuntimeStatus.Completed,
                        GraphTaskActivationCondition.WhenOutput whenOutput => dep.State.RuntimeStatus == GraphTaskRuntimeStatus.Completed && whenOutput.Predicate(dep.State.Output ?? dep.State.Artifact),
                        GraphTaskActivationCondition.WhenIgnored => dep.State.RuntimeStatus is GraphTaskRuntimeStatus.Ignored,
                        _ => false,
                    };
                    if (!conditionMet)
                        yield return condition.DependencyTaskId;
                }
            }
            yield break;
        }

        if (task.JoinPolicy is GraphTaskJoinPolicy.Any)
        {
            // Blocked only if NO predecessor has completed
            if (!task.DependsOn.Any(dep => _tasksById.TryGetValue(dep, out var d) && d.State.RuntimeStatus == GraphTaskRuntimeStatus.Completed))
                foreach (var dep in task.DependsOn)
                    yield return dep;
            yield break;
        }

        foreach (var dep in task.DependsOn)
        {
            if (!_tasksById.TryGetValue(dep, out var depTask) || depTask.State.RuntimeStatus != GraphTaskRuntimeStatus.Completed)
                yield return dep;
        }
    }

    private GraphTaskSchedulingStatus GetSchedulingStatus(GraphTask task) =>
        task.State.RuntimeStatus switch
        {
            GraphTaskRuntimeStatus.Completed => GraphTaskSchedulingStatus.Completed,
            GraphTaskRuntimeStatus.Failed => GraphTaskSchedulingStatus.Failed,
            GraphTaskRuntimeStatus.Ignored => GraphTaskSchedulingStatus.Ignored,
            GraphTaskRuntimeStatus.InProgress => GraphTaskSchedulingStatus.Active,
            GraphTaskRuntimeStatus.Suspended => GraphTaskSchedulingStatus.Active,
            _ => GetPendingDependencies(task).Any() ? GraphTaskSchedulingStatus.Blocked : GraphTaskSchedulingStatus.Ready,
        };

    private string? GetBlockingReason(GraphTask task) => BuildBlockingReason(task, []);

    private string? BuildBlockingReason(GraphTask task, HashSet<string> visited)
    {
        if (!visited.Add(task.Id))
            return $"cyclic_dependency: {task.Id}";

        var scheduling = GetSchedulingStatus(task);

        if (scheduling == GraphTaskSchedulingStatus.Blocked)
        {
            var pending = GetPendingDependencies(task).ToList();
            return pending.Count == 0
                ? "waiting_for_runtime_transition"
                : string.Join(
                    "; ",
                    pending.Select(dep =>
                    {
                        var reason = BuildDependencyReason(dep, visited);
                        return string.IsNullOrWhiteSpace(reason) ? $"waiting_for: {dep}" : $"waiting_for: {dep} -> {reason}";
                    })
                );
        }

        if (scheduling == GraphTaskSchedulingStatus.Failed)
            return string.IsNullOrWhiteSpace(task.State.Error) ? "task_failed" : task.State.Error;

        if (scheduling == GraphTaskSchedulingStatus.Ignored)
            return string.IsNullOrWhiteSpace(task.State.IgnoredBecause) ? "task_ignored" : task.State.IgnoredBecause;

        return null;
    }

    private string? BuildDependencyReason(string depId, HashSet<string> visited) => _tasksById.TryGetValue(depId, out var depTask) ? BuildBlockingReason(depTask, new HashSet<string>(visited, StringComparer.Ordinal)) : null;

    private void PropagateIgnored(IEnumerable<string> sourceIds, bool failedDependency)
    {
        var queue = new Queue<string>(sourceIds.Distinct(StringComparer.Ordinal));

        while (queue.Count > 0)
        {
            var sourceId = queue.Dequeue();
            foreach (var task in _orderedTasks.Where(t => t.State.RuntimeStatus == GraphTaskRuntimeStatus.Pending && t.DependsOn.Contains(sourceId, StringComparer.Ordinal)))
            {
                task.State = task.State with { RuntimeStatus = GraphTaskRuntimeStatus.Ignored, IgnoredBecause = failedDependency ? $"blocked_by_failed_dependency: {sourceId}" : $"blocked_by_ignored_dependency: {sourceId}" };
                queue.Enqueue(task.Id);
            }
        }
    }

    /// <summary>
    /// Marks tasks as Ignored when they have activation conditions but ALL condition predecessors
    /// are in terminal states and no condition is met (i.e. the branch was not taken).
    /// Skip propagates to downstream tasks via the existing PropagateIgnored mechanism.
    /// </summary>
    private void PropagateConditionalSkip()
    {
        List<string>? skipped = null;

        foreach (var task in _orderedTasks)
        {
            if (task.State.RuntimeStatus != GraphTaskRuntimeStatus.Pending)
                continue;

            if (task.ActivationConditions is not { Count: > 0 } conditions)
                continue;

            // All condition predecessors must be in a terminal state
            var allTerminal = conditions.All(c => _tasksById.TryGetValue(c.DependencyTaskId, out var dep) && dep.State.RuntimeStatus is GraphTaskRuntimeStatus.Completed or GraphTaskRuntimeStatus.Failed or GraphTaskRuntimeStatus.Ignored);

            if (!allTerminal)
                continue;

            // Predecessors are done but conditions are not met → skip
            if (!AreActivationConditionsMet(conditions))
            {
                task.State = task.State with { RuntimeStatus = GraphTaskRuntimeStatus.Ignored, IgnoredBecause = $"conditional_skip: conditions not met for dependencies [{string.Join(", ", conditions.Select(c => c.DependencyTaskId))}]" };
                (skipped ??= []).Add(task.Id);
            }
        }

        if (skipped is { Count: > 0 })
            PropagateIgnored(skipped, failedDependency: false);
    }

    /// <summary>
    /// Called by the host when a task in a race group completes successfully.
    /// Marks all still-pending sibling tasks in the same race group as Ignored.
    /// Returns the ids of any in-progress siblings that the host should cancel externally.
    /// </summary>
    public IReadOnlyList<string> CancelRaceGroupSiblings(string winnerTaskId, string raceGroupId)
    {
        var inProgressSiblings = new List<string>();

        foreach (var task in _orderedTasks)
        {
            if (task.Id == winnerTaskId || task.RaceGroupId != raceGroupId)
                continue;

            if (task.State.RuntimeStatus == GraphTaskRuntimeStatus.Pending)
            {
                task.State = task.State with { RuntimeStatus = GraphTaskRuntimeStatus.Ignored, IgnoredBecause = $"race_lost: winner was {winnerTaskId}" };
            }
            else if (task.State.RuntimeStatus == GraphTaskRuntimeStatus.InProgress)
            {
                inProgressSiblings.Add(task.Id);
            }
        }

        return inProgressSiblings;
    }

    private GraphTask RequireActiveTask(string taskId)
    {
        if (!_tasksById.TryGetValue(taskId, out var task) || task.State.RuntimeStatus != GraphTaskRuntimeStatus.InProgress)
        {
            throw new InvalidOperationException($"Task '{taskId}' is not currently in progress (status: {_tasksById.GetValueOrDefault(taskId)?.State.RuntimeStatus.ToString() ?? "unknown"}).");
        }

        return task;
    }
}
