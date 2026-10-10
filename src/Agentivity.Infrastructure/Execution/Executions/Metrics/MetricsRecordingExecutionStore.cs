namespace Agentivity.Infrastructure.Execution.Executions.Metrics;

/// <summary>
/// Decorator around <see cref="IExecutionRecordStore"/> — the single choke point for "an execution
/// just changed" across all ~10 call sites that mark one active/paused/completed/failed
/// (<c>RunsService.cs</c>, <c>SessionsEndpoints.cs</c>, <c>TeamResumableEngine.cs</c>,
/// <c>StandaloneAgentResumableEngine.cs</c> — none of them go through a repository, they all take
/// <see cref="IExecutionRecordStore"/> directly). Delegates every member to the real store, then
/// fires <see cref="IExecutionMetricsRecorder.RecordIfTerminalAsync"/> best-effort on whatever
/// record resulted from the write — the recorder itself is a no-op unless that record is a fresh
/// successful completion.
/// </summary>
public sealed class MetricsRecordingExecutionStore(IExecutionRecordStore inner, IExecutionMetricsRecorder recorder) : IExecutionRecordStore
{
    private readonly IExecutionRecordStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IExecutionMetricsRecorder _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));

    public async ValueTask SaveAsync(ExecutionRecord record, CancellationToken ct = default)
    {
        await _inner.SaveAsync(record, ct).ConfigureAwait(false);
        await _recorder.RecordIfTerminalAsync(record, ct).ConfigureAwait(false);
    }

    public async ValueTask<ExecutionRecord?> GetAsync(string executionId, CancellationToken ct = default) =>
        await _inner.GetAsync(executionId, ct).ConfigureAwait(false);

    public async ValueTask UpdateChannelAsync(string executionId, string channelType, string threadId, CancellationToken ct = default) =>
        await _inner.UpdateChannelAsync(executionId, channelType, threadId, ct).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ExecutionRecord>> ListActiveAsync(CancellationToken ct = default) =>
        await _inner.ListActiveAsync(ct).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<ExecutionRecord>> ListByStatesAsync(IReadOnlyCollection<string> states, CancellationToken ct = default) =>
        await _inner.ListByStatesAsync(states, ct).ConfigureAwait(false);

    // ── IStore<ExecutionRecord> ─────────────────────────────────────────────

    public async ValueTask AddAsync(ExecutionRecord record, CancellationToken cancellationToken = default)
    {
        await _inner.AddAsync(record, cancellationToken).ConfigureAwait(false);
        await _recorder.RecordIfTerminalAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ExecutionRecord?> FindAsync(string id, CancellationToken cancellationToken = default) =>
        _inner.FindAsync(id, cancellationToken);

    public ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(id, cancellationToken);

    public ValueTask<IReadOnlyList<ExecutionRecord>> FindAllAsync(CancellationToken cancellationToken = default) =>
        _inner.FindAllAsync(cancellationToken);

    public ValueTask<IReadOnlyList<ExecutionRecord>> FindWhereAsync(Func<ExecutionRecord, bool> predicate, CancellationToken cancellationToken = default) =>
        _inner.FindWhereAsync(predicate, cancellationToken);

    public async ValueTask UpdateAsync(string id, Func<ExecutionRecord, ExecutionRecord> mutator, CancellationToken cancellationToken = default)
    {
        ExecutionRecord? updated = null;
        await _inner.UpdateAsync(id, record =>
        {
            updated = mutator(record);
            return updated;
        }, cancellationToken).ConfigureAwait(false);

        if (updated is not null)
            await _recorder.RecordIfTerminalAsync(updated, cancellationToken).ConfigureAwait(false);
    }
}
