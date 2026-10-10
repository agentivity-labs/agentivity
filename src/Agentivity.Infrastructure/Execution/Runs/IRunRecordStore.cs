using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// Durable store for the generic <see cref="RunRecord"/>s — the persisted truth of the run tree
/// AND, since EPIC RunRecord-unifié, of every engine's progress (via <see cref="RunRecord.Payload"/>).
/// The in-memory <see cref="IRunRepository"/> writes through to this store on every lifecycle
/// transition; on process restart, records are lazily reloaded from here.
/// <para>
/// This contract is being promoted to THE single run-persistence store, replacing
/// <c>ITeamRunStore</c>, <c>IAgentRunStore</c>, <c>IWorkflowRunStore</c> and <c>IWaitingRunStore</c>
/// (removed engine by engine in later stories of the same EPIC) — hence
/// <see cref="ListByOwnerAsync"/>, which consolidates their per-owner listing.
/// </para>
/// <para>
/// Extends <see cref="IStore{TRecord}"/> so it can be handed directly to
/// <see cref="AbstractRepository{TEntity,TRecord}"/> — purely additive, <c>SaveAsync</c>/<c>GetAsync</c>
/// are kept alongside the inherited <c>AddAsync</c>/<c>FindAsync</c> for existing callers (zero breaking change).
/// </para>
/// </summary>
public interface IRunRecordStore : IStore<RunRecord>
{
    /// <summary>Saves or updates the record (moved to its current state directory).</summary>
    ValueTask SaveAsync(RunRecord record, CancellationToken cancellationToken = default);

    /// <summary>Loads a record by run id. Null if not found.</summary>
    ValueTask<RunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Lists the direct children of <paramref name="parentRunId"/> (persisted edge), newest first.</summary>
    ValueTask<IReadOnlyList<RunRecord>> ListChildrenAsync(string parentRunId, CancellationToken cancellationToken = default);

    /// <summary>Lists all records currently in <paramref name="state"/> (startup recovery scan), newest first.</summary>
    ValueTask<IReadOnlyList<RunRecord>> ListByStateAsync(Domain.Run.RunStatus state, CancellationToken cancellationToken = default);

    /// <summary>Lists all records owned by <paramref name="entityId"/> (e.g. all runs of one agent/team/workflow), newest first.</summary>
    ValueTask<IReadOnlyList<RunRecord>> ListByOwnerAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the record. No-op if absent.</summary>
    ValueTask DeleteAsync(string runId, CancellationToken cancellationToken = default);
}
