using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// Disk-backed <see cref="IRunRecordStore"/> — the sole persistence mechanism for
/// <see cref="RunRecord"/>. Thin subclass of <see cref="AbstractDiskStore{TRecord}"/>: partitioned
/// by lifecycle state (<c>{basePath}/{state}/{runId}.json</c>, default base
/// <c>_cache/runs/records</c>), move-on-save when state changes, per-runId locking — all owned by
/// the base. <see cref="IRunRecordStore"/>'s own method names (<c>SaveAsync</c>/<c>GetAsync</c>)
/// are kept as-is (not renamed to the base's <c>AddAsync</c>/<c>FindAsync</c>) — this is a
/// long-standing, widely-consumed public interface; renaming it would ripple across every
/// endpoint/engine that injects it for zero benefit.
/// </summary>
public sealed class DiskRunRecordStore : AbstractDiskStore<RunRecord>, IRunRecordStore
{
    public DiskRunRecordStore(string relativeCachePath = "_cache/runs/records")
        : base(relativeCachePath, r => r.RunId, r => r.Status.ToString().ToLowerInvariant())
    {
        foreach (var state in Enum.GetValues<RunStatus>())
            Directory.CreateDirectory(Path.Combine(BasePath, state.ToString().ToLowerInvariant()));
    }

    public ValueTask SaveAsync(RunRecord record, CancellationToken cancellationToken = default) =>
        AddAsync(record, cancellationToken);

    public ValueTask<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default) =>
        FindAsync(runId, cancellationToken);

    /// <summary>Lists every record currently in the given state's directory — reads only that one subfolder.</summary>
    public ValueTask<IReadOnlyList<RunRecord>> ListByStateAsync(RunStatus state, CancellationToken cancellationToken = default) =>
        ListPartitionAsync(state.ToString().ToLowerInvariant(), cancellationToken);

    public async ValueTask<IReadOnlyList<RunRecord>> ListChildrenAsync(string parentRunId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRunId);
        var matches = await FindWhereAsync(r => string.Equals(r.ParentRunId, parentRunId, StringComparison.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
        return [.. matches.OrderByDescending(r => r.CreatedAt)];
    }

    public async ValueTask<IReadOnlyList<RunRecord>> ListByOwnerAsync(string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        var matches = await FindWhereAsync(r => string.Equals(r.EntityId, entityId, StringComparison.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
        return [.. matches.OrderByDescending(r => r.CreatedAt)];
    }
}
