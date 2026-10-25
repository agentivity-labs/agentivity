namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Summarises task counts by scheduling status for quick health checks and telemetry.
/// Produced by <see cref="GraphRuntime.BuildStatusSummary"/>.
/// </summary>
/// <param name="TotalCount">Total number of tasks in the graph.</param>
/// <param name="CompletedCount">Tasks in <see cref="GraphTaskSchedulingStatus.Completed"/>.</param>
/// <param name="ActiveCount">Tasks currently executing (excludes suspended tasks).</param>
/// <param name="SuspendedCount">Tasks in <see cref="GraphTaskRuntimeStatus.Suspended"/> awaiting external resumption.</param>
/// <param name="ReadyCount">Tasks in <see cref="GraphTaskSchedulingStatus.Ready"/>.</param>
/// <param name="BlockedCount">Tasks in <see cref="GraphTaskSchedulingStatus.Blocked"/>.</param>
/// <param name="FailedCount">Tasks in <see cref="GraphTaskSchedulingStatus.Failed"/>.</param>
/// <param name="IgnoredCount">Tasks in <see cref="GraphTaskSchedulingStatus.Ignored"/>.</param>
public sealed record GraphStatusSummary(int TotalCount, int CompletedCount, int ActiveCount, int SuspendedCount, int ReadyCount, int BlockedCount, int FailedCount, int IgnoredCount)
{
    /// <summary>
    /// Returns true when no task is actively running or waiting to run.
    /// Suspended tasks are considered terminal from the scheduler's perspective —
    /// they require external resumption and will not progress on their own.
    /// </summary>
    public bool IsTerminal => TotalCount > 0 && ActiveCount == 0 && ReadyCount == 0 && BlockedCount == 0;

    /// <summary>Returns true when all tasks completed successfully (no failures, ignores, or suspensions).</summary>
    public bool IsSuccessful => IsTerminal && FailedCount == 0 && IgnoredCount == 0 && SuspendedCount == 0;
}
