namespace Agentivity.Infrastructure.Execution.Executions.Metrics;

/// <summary>
/// Computes and persists an <see cref="ExecutionMetricsRecord"/> the first time an execution is
/// observed as terminal — see <see cref="MetricsRecordingExecutionStore"/>, the decorator around
/// <see cref="IExecutionRecordStore"/> that every completion write (agent/team/workflow, ~10 call
/// sites) already passes through.
/// </summary>
public interface IExecutionMetricsRecorder
{
    /// <summary>
    /// No-op unless <paramref name="execution"/> is a successful completion with no metrics
    /// recorded yet for its <see cref="ExecutionRecord.ExecutionId"/> — idempotent by
    /// construction, safe to call on every persist of the same execution without double-counting.
    /// </summary>
    ValueTask RecordIfTerminalAsync(ExecutionRecord execution, CancellationToken cancellationToken = default);
}
