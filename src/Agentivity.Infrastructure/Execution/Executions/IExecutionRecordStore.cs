using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Executions;

/// <summary>
/// Extends <see cref="IStore{TRecord}"/> so it can be handed directly to
/// <see cref="AbstractRepository{TEntity,TRecord}"/> — purely additive, <c>SaveAsync</c>/<c>GetAsync</c>
/// are kept alongside the inherited <c>AddAsync</c>/<c>FindAsync</c> for existing callers (zero breaking change).
/// </summary>
public interface IExecutionRecordStore : IStore<ExecutionRecord>
{
    ValueTask SaveAsync(ExecutionRecord record, CancellationToken ct = default);
    ValueTask<ExecutionRecord?> GetAsync(string executionId, CancellationToken ct = default);
    ValueTask UpdateChannelAsync(string executionId, string channelType, string threadId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ExecutionRecord>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>Lists every record currently in one of <paramref name="states"/> (e.g. "active", "paused", "failed") — generalizes <see cref="ListActiveAsync"/>.</summary>
    ValueTask<IReadOnlyList<ExecutionRecord>> ListByStatesAsync(IReadOnlyCollection<string> states, CancellationToken ct = default);
}
