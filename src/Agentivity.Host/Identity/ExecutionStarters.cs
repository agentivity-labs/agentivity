using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Identity;
using Agentivity.Infrastructure.Repository;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;

namespace Agentivity.Host.Identity;

/// <summary>Who started an execution, and the space it is in, from the execution itself, one of its runs, or its stream.</summary>
public sealed class ExecutionStarters(IRepository<ExecutionContext, ExecutionRecord> executions, IRepository<RunContext, RunRecord> runs) : IExecutionStarters, IExecutionSpaces
{
    async Task<string?> IExecutionSpaces.OfExecutionAsync(string executionId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(executionId) ? null : (await executions.FindAsync(executionId, cancellationToken).ConfigureAwait(false))?.SpaceId;

    async Task<string?> IExecutionSpaces.OfRunAsync(string runId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(runId) || await runs.FindAsync(runId, cancellationToken).ConfigureAwait(false) is not { } run ? null : run.Execution.SpaceId;

    async Task<string?> IExecutionSpaces.OfStreamAsync(string streamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            return null;
        var owning = await executions.FindWhereAsync(e => string.Equals(e.StreamId, streamId, StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        return owning.Count == 1 ? owning[0].SpaceId : null;
    }

    public async Task<Caller?> OfExecutionAsync(string executionId, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(executionId) ? null : (await executions.FindAsync(executionId, cancellationToken).ConfigureAwait(false))?.StartedBy;

    public async Task<Caller?> OfRunAsync(string runId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId) || await runs.FindAsync(runId, cancellationToken).ConfigureAwait(false) is not { } run)
            return null;
        return await OfExecutionAsync(run.Execution.ExecutionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Caller?> OfStreamAsync(string streamId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            return null;
        var owning = await executions.FindWhereAsync(e => string.Equals(e.StreamId, streamId, StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        return owning.Count == 1 ? owning[0].StartedBy : null;
    }
}
