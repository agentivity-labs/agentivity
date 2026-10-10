using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Executions.Metrics;

/// <summary>
/// Durable store for <see cref="ExecutionMetricsRecord"/> — write-once analytics facts, kept
/// separate from <see cref="IExecutionRecordStore"/> so aggregate queries ("time saved this
/// week/year") never need to touch the much heavier run-level trace blobs. Extends
/// <see cref="IStore{TRecord}"/> for the generic CRUD surface; <see cref="GetAsync"/>/
/// <see cref="ListSinceAsync"/> are thin, named convenience wrappers for the two read shapes
/// actually needed (idempotency check by execution id, aggregation by completion date).
/// </summary>
public interface IExecutionMetricsStore : IStore<ExecutionMetricsRecord>
{
    /// <summary>Loads a record by execution id. Null if no metrics were recorded for this execution (e.g. it never completed successfully).</summary>
    ValueTask<ExecutionMetricsRecord?> GetAsync(string executionId, CancellationToken cancellationToken = default);

    /// <summary>Lists every record completed at or after <paramref name="sinceUtc"/>, for aggregation (e.g. "this week"/"this year").</summary>
    ValueTask<IReadOnlyList<ExecutionMetricsRecord>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);
}
