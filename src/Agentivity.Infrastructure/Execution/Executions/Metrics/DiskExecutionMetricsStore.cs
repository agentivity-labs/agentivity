using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Executions.Metrics;

/// <summary>
/// Disk-backed <see cref="IExecutionMetricsStore"/> — thin subclass of
/// <see cref="AbstractDiskStore{TRecord}"/>, same shape as <see cref="DiskExecutionRecordStore"/>.
/// Partitioned by completion month (<c>{basePath}/{yyyy-MM}/{executionId}.json</c>) rather than by
/// lifecycle state (there is only one lifecycle: written once, never mutated) — this keeps "this
/// week"/"this year" aggregation cheap as the store grows, since <see cref="ListSinceAsync"/> only
/// needs to reason about a handful of month folders rather than scanning every record ever
/// written.
/// </summary>
public sealed class DiskExecutionMetricsStore : AbstractDiskStore<ExecutionMetricsRecord>, IExecutionMetricsStore
{
    public DiskExecutionMetricsStore(string relativeCachePath = "_cache/runs/metrics")
        : base(relativeCachePath, r => r.ExecutionId, r => r.CompletedAtUtc.ToString("yyyy-MM"))
    {
    }

    public ValueTask<ExecutionMetricsRecord?> GetAsync(string executionId, CancellationToken cancellationToken = default) =>
        FindAsync(executionId, cancellationToken);

    public async ValueTask<IReadOnlyList<ExecutionMetricsRecord>> ListSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        var results = new List<ExecutionMetricsRecord>();
        var cursor = new DateTime(sinceUtc.Year, sinceUtc.Month, 1);
        var end = DateTime.UtcNow;
        while (cursor <= end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partition = await ListPartitionAsync(cursor.ToString("yyyy-MM"), cancellationToken).ConfigureAwait(false);
            results.AddRange(partition.Where(r => r.CompletedAtUtc >= sinceUtc));
            cursor = cursor.AddMonths(1);
        }
        return [.. results.OrderByDescending(r => r.CompletedAtUtc)];
    }
}
