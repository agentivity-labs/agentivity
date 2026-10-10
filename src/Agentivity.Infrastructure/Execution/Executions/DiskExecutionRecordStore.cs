using System;
using System.Collections.Generic;
using System.Text;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Executions;

/// <summary>
/// Stores <see cref="ExecutionRecord"/> as flat JSON files: <c>_cache/runs/executions/{executionId}.json</c>.
/// Thin subclass of <see cref="AbstractDiskStore{TRecord}"/> (flat layout, no partitioning) — the
/// per-id locking it inherits is what makes <see cref="UpdateChannelAsync"/> atomic, fixing a real
/// pre-existing race (the old hand-rolled version did an unlocked Get-then-Save).
/// <see cref="IExecutionRecordStore"/>'s own method names (<c>SaveAsync</c>/<c>GetAsync</c>) are kept
/// as-is, not renamed to the base's <c>AddAsync</c>/<c>FindAsync</c> — long-standing public interface.
/// </summary>
public sealed class DiskExecutionRecordStore : AbstractDiskStore<ExecutionRecord>, IExecutionRecordStore
{
    public DiskExecutionRecordStore()
        : base("_cache/runs/executions", r => r.ExecutionId)
    {
    }

    public ValueTask SaveAsync(ExecutionRecord record, CancellationToken ct = default) =>
        AddAsync(record, ct);

    public ValueTask<ExecutionRecord?> GetAsync(string executionId, CancellationToken ct = default) =>
        FindAsync(executionId, ct);

    public ValueTask UpdateChannelAsync(string executionId, string channelType, string threadId, CancellationToken ct = default) =>
        UpdateAsync(executionId, record =>
        {
            var channels = new Dictionary<string, string>(record.Channels, StringComparer.OrdinalIgnoreCase) { [channelType] = threadId };
            return record with { Channels = channels, UpdatedAt = DateTimeOffset.UtcNow };
        }, ct);

    public ValueTask<IReadOnlyList<ExecutionRecord>> ListActiveAsync(CancellationToken ct = default) =>
        FindWhereAsync(r => r.State == "active", ct);

    public ValueTask<IReadOnlyList<ExecutionRecord>> ListByStatesAsync(IReadOnlyCollection<string> states, CancellationToken ct = default) =>
        FindWhereAsync(r => states.Contains(r.State, StringComparer.OrdinalIgnoreCase), ct);
}
