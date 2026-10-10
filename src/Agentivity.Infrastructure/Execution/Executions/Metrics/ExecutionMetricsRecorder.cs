using Agentivity.Infrastructure.Execution.Runs;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Execution.Executions.Metrics;

/// <summary>
/// Default <see cref="IExecutionMetricsRecorder"/> — checks <see cref="IExecutionMetricsStore.GetAsync"/>
/// for idempotency, resolves every <see cref="RunRecord"/> the execution references (via
/// <see cref="ExecutionRecord.RunIds"/>), computes via <see cref="ExecutionMetricsCalculator"/>,
/// persists. Best-effort: never lets a metrics failure surface to the caller, since the caller is
/// <see cref="MetricsRecordingExecutionStore"/> on the hot path of every real execution.
/// </summary>
public sealed class ExecutionMetricsRecorder(
    IExecutionMetricsStore store,
    IRunRecordStore runRecordStore,
    ILogger<ExecutionMetricsRecorder>? logger = null
) : IExecutionMetricsRecorder
{
    private readonly IExecutionMetricsStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IRunRecordStore _runRecordStore = runRecordStore ?? throw new ArgumentNullException(nameof(runRecordStore));

    public async ValueTask RecordIfTerminalAsync(ExecutionRecord execution, CancellationToken cancellationToken = default)
    {
        try
        {
            if (execution.State != "completed")
                return;

            var existing = await _store.GetAsync(execution.ExecutionId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return;

            var runs = new List<RunRecord>();
            foreach (var runId in execution.RunIds)
            {
                var run = await _runRecordStore.GetAsync(runId, cancellationToken).ConfigureAwait(false);
                if (run is not null)
                    runs.Add(run);
            }

            var metrics = ExecutionMetricsCalculator.Compute(execution, runs);
            if (metrics is null)
                return;

            await _store.AddAsync(metrics, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not record execution metrics for {ExecutionId}.", execution.ExecutionId);
        }
    }
}
