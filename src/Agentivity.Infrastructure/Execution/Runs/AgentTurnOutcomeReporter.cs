using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Repository;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// Single write point for a standalone agent run's status/event bookkeeping once a turn ends —
/// whether that's the run's very first turn (<c>RunsService</c>/<c>ExecutionService</c>, in
/// Agentivity.Host) or a turn reached via HIL resume (<c>StandaloneAgentResumableEngine</c>, in
/// Agentivity.Infrastructure.Fabric). Before this existed, each of those three call sites
/// reimplemented the same "WaitingFor → still waiting, else → completed + <c>run_completed</c>
/// event" branch independently; the resume path's copy was missing entirely, which left a
/// resumed run's status stuck at whatever the FIRST suspension left it at, with no completion
/// signal ever reaching the frontend.
/// <para>
/// Deliberately scoped to only the two things every call site agrees on byte-for-byte: the
/// <see cref="RunContext"/> status transition and the <c>run_completed</c> event. Execution-record
/// state and graph-step outputs are left to callers, since <c>RunsService</c> and
/// <c>ExecutionService</c> track the execution record through two different abstractions
/// (<c>IExecutionRecordStore</c> vs <c>IRepository&lt;ExecutionContext, ExecutionRecord&gt;</c>) —
/// forcing them onto one shape here would be a bigger, riskier change than this fix calls for.
/// </para>
/// </summary>
public static class AgentTurnOutcomeReporter
{
    /// <summary>
    /// Applies the status transition for this turn and, when the run actually finished (not
    /// suspended), publishes <c>run_completed</c>. Returns <see langword="true"/> when the run
    /// completed — callers use this to decide whether to also run their own completion-only
    /// bookkeeping (execution record state, graph step outputs) alongside this shared part.
    /// </summary>
    public static async ValueTask<bool> ReportAsync(
        IRepository<RunContext, RunRecord>? runRepository,
        IRunEventBus? eventBus,
        string runId,
        string agentId,
        string? executionId,
        ExecutableStatus status,
        string response,
        CancellationToken ct = default,
        string? streamId = null
    )
    {
        if (status == ExecutableStatus.WaitingFor)
        {
            await SetRunStateAsync(runRepository, runId, RunStatus.WaitingForInput, completedAt: null, ct).ConfigureAwait(false);
            return false;
        }

        var completedAt = DateTimeOffset.UtcNow;
        await SetRunStateAsync(runRepository, runId, RunStatus.Completed, completedAt, ct).ConfigureAwait(false);
        eventBus?.Publish(
            new RunEvent(
                RunId: runId,
                DefinitionId: agentId,
                RunKind: RunKind.Agent,
                Kind: RunEventKind.run_completed,
                EventName: "agent_run_completed",
                OccurredAtUtc: completedAt,
                Payload: new { content = response },
                Terminal: true,
                ExecutionId: executionId,
                StreamId: streamId
            )
        );
        return true;
    }

    private static async ValueTask SetRunStateAsync(IRepository<RunContext, RunRecord>? runRepository, string runId, RunStatus state, DateTimeOffset? completedAt, CancellationToken ct)
    {
        if (runRepository is null)
            return;
        var ctx = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);
        if (ctx is null)
            return;
        ctx.SetStatus(state, error: null, completedAt);
        await runRepository.AddAsync(ctx, ct).ConfigureAwait(false);
    }
}
