using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Repository;
using Microsoft.Extensions.Logging;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;

namespace Agentivity.Infrastructure.Interactions;

/// <summary>Outcome of a unified resume/recover attempt.</summary>
public sealed record RunResumeOutcome(bool Success, string? Error = null, string? RootRunId = null);

/// <summary>Outcome of the startup recovery scan.</summary>
public sealed record RunRecoveryReport(int Rearmed, int Interrupted);

/// <summary>
/// THE single entry point for resuming any suspended run (EPIC durable-execution, Story 2/3).
/// HIL pause, platform restart and crash recovery are the same functionality: rebuild from
/// durable state and re-enter through one path.
/// <para>
/// One execution is a tree of runs, and a run that waits makes every run above it wait. When what a run was waiting for
/// has come (<see cref="ResumeAsync"/> for a person's answer, <see cref="WakeAsync"/> for anything else), it is not the top
/// of the tree that is taken up again: it is the run that called the one that was waiting, its parent, which calls it
/// again (a run on its own, at the top, is taken up itself). When that run has finished, its own parent is taken up again
/// in its turn, finds it finished, and goes on; and so on up to the top. A run is taken up again only once the run it was
/// waiting for has finished, whatever the depth of the tree.
/// </para>
/// <para>
/// Taking a run up again is the same for all: gate on its lifecycle state, rehydrate its <see cref="RunContext"/> (same
/// ExecutionId/StreamId via the injected <c>IRepository{ExecutionContext,ExecutionRecord}</c>), and delegate the re-entry
/// to the <see cref="IResumableEngine"/> registered for its kind. Recovery after a crash (<see cref="RecoverAsync"/>) is
/// the one case that starts from the top of the branch: nothing has come, the whole branch is re-entered from its last
/// checkpoint.
/// </para>
/// <para>
/// At startup, <see cref="RecoverAllAsync"/> scans the durable records: runs waiting for a human
/// are re-armed (hot registry entries rebuilt), runs orphaned mid-flight by a crash are marked
/// <see cref="RunStatus.Interrupted"/> — re-enterable manually via <see cref="RecoverAsync"/>.
/// </para>
/// </summary>
public sealed class DurableRunService(
    IRepository<RunContext, RunRecord> runRepository,
    IEnumerable<IResumableEngine> engines,
    IRepository<ExecutionContext, ExecutionRecord>? executionRepository = null,
    IRunRecordStore? recordStore = null,
    ILogger<DurableRunService>? logger = null
)
{
    // Backstop against a corrupt/cyclic persisted tree; deeper nesting is already rejected at run time.
    private const int MaxWalkDepth = 10;

    /// <summary>Takes up again what waits for <paramref name="runId"/>, the run that asked a person something, now that they answered.</summary>
    public ValueTask<RunResumeOutcome> ResumeAsync(Guid runId, InteractionResponse response, CancellationToken ct = default) => TakeUpWhatWaitsForAsync(runId, response, ct);

    /// <summary>
    /// Takes up again what waits for <paramref name="runId"/>, when what that run was waiting for has come and it is not a
    /// person's answer (a job that ended, a call that came in…): the run that was waiting is named, nothing else.
    /// </summary>
    public ValueTask<RunResumeOutcome> WakeAsync(Guid runId, CancellationToken ct = default) => TakeUpWhatWaitsForAsync(runId, response: null, ct);

    private static bool IsWaiting(RunStatus status) => status is RunStatus.Paused or RunStatus.WaitingForInput;

    /// <summary>
    /// What <paramref name="runId"/> was waiting for has come: the run that called it is taken up again and calls it again
    /// (that run has everything needed to do so: what it was given, where it stands). A run with no parent is taken up
    /// itself.
    /// </summary>
    private async ValueTask<RunResumeOutcome> TakeUpWhatWaitsForAsync(Guid runId, InteractionResponse? response, CancellationToken ct)
    {
        var waiting = await runRepository.FindAsync(runId.ToString("D"), ct).ConfigureAwait(false);
        if (waiting is null)
            return new RunResumeOutcome(false, $"Run '{runId}' is unknown (no live entry and no durable record).");

        var run = waiting;
        if (waiting.ParentRunId is not null)
        {
            var parent = await runRepository.FindAsync(waiting.ParentRunId, ct).ConfigureAwait(false);
            if (parent is null)
                logger?.LogWarning("Run tree is broken: parent {ParentRunId} of {RunId} has no entry/record — resuming from {RunId}.", waiting.ParentRunId, waiting.RunId, waiting.RunId);
            else
                run = parent;
        }

        return await ReenterAsync(run, response, IsWaiting, "not waiting for input", thenItsParent: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Once <paramref name="settled"/> has ended, <paramref name="run"/> has finished or is waiting again. Finished, and
    /// called by a run that is waiting for it: that run is taken up again in its turn.
    /// </summary>
    private async Task TakeUpItsParentWhenDoneAsync(RunContext run, Task settled)
    {
        try
        {
            await settled.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The engine reports its own failures on the run; the state of the run is what is read below.
            logger?.LogDebug(ex, "Run {RunId} ended with an exception after being taken up again.", run.RunId);
        }

        try
        {
            var now = await runRepository.FindAsync(run.RunId, CancellationToken.None).ConfigureAwait(false);
            if (now?.ParentRunId is null || now.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled))
                return;

            var parent = await runRepository.FindAsync(now.ParentRunId, CancellationToken.None).ConfigureAwait(false);
            if (parent is null || !IsWaiting(parent.Status))
                return;

            var outcome = await ReenterAsync(parent, response: null, IsWaiting, "not waiting", thenItsParent: true, CancellationToken.None).ConfigureAwait(false);
            if (!outcome.Success)
                logger?.LogWarning("Run {ParentRunId} could not be taken up again after its child {RunId} finished: {Error}", parent.RunId, run.RunId, outcome.Error);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Taking up the parent of run {RunId} again failed.", run.RunId);
        }
    }

    /// <summary>
    /// Manually re-enters a run interrupted by a crash/restart, from its last checkpoint
    /// (completed work is never re-executed). No interaction response is involved.
    /// </summary>
    public ValueTask<RunResumeOutcome> RecoverAsync(Guid runId, CancellationToken ct = default) =>
        ReenterBranchRootAsync(runId, response: null, gate: static s => s is RunStatus.Interrupted, gateError: "not interrupted", ct, recovering: true);

    /// <summary>
    /// Startup recovery scan over the durable run records:
    /// <list type="bullet">
    /// <item>WaitingForInput / Paused → re-armed (hot registry entry rebuilt) so pending HIL
    /// questions remain answerable after the restart;</item>
    /// <item>Running / Pending → orphaned by the previous process — marked
    /// <see cref="RunStatus.Interrupted"/> (manual re-entry via <see cref="RecoverAsync"/>;
    /// automatic re-entry at boot is a deliberate non-default).</item>
    /// </list>
    /// </summary>
    public async ValueTask<RunRecoveryReport> RecoverAllAsync(CancellationToken ct = default)
    {
        if (recordStore is null)
            return new RunRecoveryReport(0, 0);

        var rearmed = 0;
        foreach (var state in new[] { RunStatus.WaitingForInput, RunStatus.Paused })
        {
            foreach (var record in await recordStore.ListByStateAsync(state, ct).ConfigureAwait(false))
            {
                if (await runRepository.FindAsync(record.RunId, ct).ConfigureAwait(false) is not null)
                    rearmed++;
            }
        }

        var interrupted = 0;
        foreach (var state in new[] { RunStatus.Running, RunStatus.Pending })
        {
            foreach (var record in await recordStore.ListByStateAsync(state, ct).ConfigureAwait(false))
            {
                var context = await runRepository.FindAsync(record.RunId, ct).ConfigureAwait(false);
                if (context is null)
                    continue;
                context.SetStatus(RunStatus.Interrupted, error: "The run was interrupted by a backend restart while executing. It can be re-entered from its last checkpoint.");
                await runRepository.AddAsync(context, ct).ConfigureAwait(false);
                interrupted++;
            }
        }

        if (rearmed > 0 || interrupted > 0)
            logger?.LogInformation("Run recovery: {Rearmed} waiting run(s) re-armed, {Interrupted} orphaned run(s) marked Interrupted.", rearmed, interrupted);

        return new RunRecoveryReport(rearmed, interrupted);
    }


    private async ValueTask<RunResumeOutcome> ReenterBranchRootAsync(Guid runId, InteractionResponse? response, Func<RunStatus, bool> gate, string gateError, CancellationToken ct, bool recovering = false)
    {
        var entry = await runRepository.FindAsync(runId.ToString("D"), ct).ConfigureAwait(false);
        if (entry is null)
            return new RunResumeOutcome(false, $"Run '{runId}' is unknown (no live entry and no durable record).");

        // Walk to the branch root: only the root is re-entered directly; suspended descendants
        // are re-entered by their parent's cascade (member tasks re-find them via ChildRunId).
        for (var depth = 0; entry.ParentRunId is not null && depth < MaxWalkDepth; depth++)
        {
            var parent = await runRepository.FindAsync(entry.ParentRunId, ct).ConfigureAwait(false);
            if (parent is null)
            {
                logger?.LogWarning("Run tree is broken: parent {ParentRunId} of {RunId} has no entry/record — resuming from {RunId}.", entry.ParentRunId, entry.RunId, entry.RunId);
                break;
            }
            entry = parent;
        }

        return await ReenterAsync(entry, response, gate, gateError, thenItsParent: false, ct, recovering).ConfigureAwait(false);
    }

    /// <summary>Takes one run up again through the engine of its kind. The same for every run, wherever it is in the tree.</summary>
    private async ValueTask<RunResumeOutcome> ReenterAsync(RunContext entry, InteractionResponse? response, Func<RunStatus, bool> gate, string gateError, bool thenItsParent, CancellationToken ct, bool recovering = false)
    {
        if (!gate(entry.Status))
            return new RunResumeOutcome(false, $"Run '{entry.RunId}' is {gateError} (status: {entry.Status}).", entry.RunId);

        var entityKind = entry.Kind.ToString().ToLowerInvariant();
        var engine = engines.FirstOrDefault(e => string.Equals(e.Kind, entityKind, StringComparison.OrdinalIgnoreCase));
        if (engine is null)
            return new RunResumeOutcome(false, $"No resumable engine is registered for run kind '{entityKind}'.", entry.RunId);

        // Nothing to go on from: said, and nothing is touched (the execution stays as it is, not set going then failed).
        if (recovering && !engine.RecoversInterrupted)
            return new RunResumeOutcome(false, $"This {entityKind} cannot go on from where it was cut off: it keeps its place only where it waits for a person. Run it again.", entry.RunId);

        // `entry` is the run's one live, cache-registered RunContext (FindAsync returns the
        // cached instance on a hit, or a freshly-reconstructed-and-recached one from the durable
        // record on a cache miss — either way, already the single source of truth every other
        // reader of the repository sees). Never build a second RunContext for the same run: a
        // parallel copy (the old Resume()+ReconcileForResume() dance) mutates independently of
        // `entry` and silently diverges from what every other reader gets back.
        // The original ExecutionContext (same ExecutionId + StreamId) is restored so events reach
        // the same SSE subscriber, in case `entry` came back from a minimal FromRecord() rebuild.
        var executionId = entry.Execution.ExecutionId;
        var executionCtx = executionRepository is not null
            ? await executionRepository.FindAsync(executionId, ct).ConfigureAwait(false)
            : null;
        entry.PrepareForResume(executionCtx);

        // The top run goes on: its execution is active again until it settles.
        var isTop = entry.ParentRunId is null && executionCtx is not null;
        if (isTop)
        {
            executionCtx!.SetState(ExecutionContext.StateOf(RunStatus.Running));
            await executionRepository!.AddAsync(executionCtx, ct).ConfigureAwait(false);
        }

        var settled = await engine.ReenterAsync(entry, response, ct).ConfigureAwait(false);
        if (isTop)
            _ = SetItsExecutionWhenSettledAsync(entry, executionCtx!, settled);
        if (thenItsParent)
            _ = TakeUpItsParentWhenDoneAsync(entry, settled);
        return new RunResumeOutcome(true, RootRunId: entry.RunId);
    }

    /// <summary>
    /// The top run of an execution was taken up again: once it has settled (finished, waiting again, or failed), its execution
    /// says so (<see cref="ExecutionContext.StateOf"/>), whatever the kind of the run.
    /// </summary>
    private async Task SetItsExecutionWhenSettledAsync(RunContext run, ExecutionContext execution, Task settled)
    {
        try
        {
            await settled.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The engine reports its own failures on the run; the state of the run is what is read below.
            logger?.LogDebug(ex, "Run {RunId} ended with an exception after being taken up again.", run.RunId);
        }

        try
        {
            var now = await runRepository.FindAsync(run.RunId, CancellationToken.None).ConfigureAwait(false) ?? run;
            execution.SetState(ExecutionContext.StateOf(now.Status));
            await executionRepository!.AddAsync(execution, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "The execution of run {RunId} could not be updated after it was taken up again.", run.RunId);
        }
    }
}
