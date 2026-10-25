namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Immutable, point-in-time projection of a single <see cref="GraphTask"/> produced by
/// <see cref="GraphRuntime.BuildSnapshot"/>. Carries scheduling identity, runtime state,
/// and the derived <see cref="SchedulingStatus"/> and <see cref="BlockingReason"/> so that
/// consumers never have to re-implement scheduling logic.
/// </summary>
/// <param name="Id">Unique task identifier within the graph.</param>
/// <param name="DependsOn">Ids of tasks that must complete before this task becomes ready.</param>
/// <param name="State">Immutable runtime state at the moment the snapshot was taken.</param>
/// <param name="SchedulingStatus">Scheduling status derived by the runtime at snapshot time.</param>
/// <param name="BlockingReason">
/// Human-readable reason why the task is blocked, failed, or ignored; null when the task
/// is pending-ready, in-progress, suspended, or completed.
/// </param>
public sealed record GraphTaskSnapshot(string Id, IReadOnlyList<string> DependsOn, GraphTaskState State, GraphTaskSchedulingStatus SchedulingStatus, string? BlockingReason);
