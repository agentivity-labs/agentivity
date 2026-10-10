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
/// Given any run id, the service walks the persisted run tree UP to the branch root
/// (invariant: a nested run is never resumed directly — its parent's re-entry cascade owns it,
/// which replaces the old per-handler "declination" logic), gates on the run's lifecycle state,
/// rehydrates the <see cref="RunContext"/> (same ExecutionId/StreamId via the injected
/// <c>IRepository{ExecutionContext,ExecutionRecord}</c>), and delegates the re-entry to the
/// <see cref="IResumableEngine"/> registered for the run's kind.
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

    /// <summary>Resumes the branch owning <paramref name="runId"/> after a human response.</summary>
    public ValueTask<RunResumeOutcome> ResumeAsync(Guid runId, InteractionResponse response, CancellationToken ct = default) =>
        ReenterBranchRootAsync(runId, response, gate: static s => s is RunStatus.Paused or RunStatus.WaitingForInput, gateError: "not waiting for input", ct);

    /// <summary>
    /// Manually re-enters a run interrupted by a crash/restart, from its last checkpoint
    /// (completed work is never re-executed). No interaction response is involved.
    /// </summary>
    public ValueTask<RunResumeOutcome> RecoverAsync(Guid runId, CancellationToken ct = default) =>
        ReenterBranchRootAsync(runId, response: null, gate: static s => s is RunStatus.Interrupted, gateError: "not interrupted", ct);

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


    private async ValueTask<RunResumeOutcome> ReenterBranchRootAsync(Guid runId, InteractionResponse? response, Func<RunStatus, bool> gate, string gateError, CancellationToken ct)
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

        if (!gate(entry.Status))
            return new RunResumeOutcome(false, $"Run '{entry.RunId}' is {gateError} (status: {entry.Status}).", entry.RunId);

        var entityKind = entry.Kind.ToString().ToLowerInvariant();
        var engine = engines.FirstOrDefault(e => string.Equals(e.Kind, entityKind, StringComparison.OrdinalIgnoreCase));
        if (engine is null)
            return new RunResumeOutcome(false, $"No resumable engine is registered for run kind '{entityKind}'.", entry.RunId);

        // `entry` is the run's one live, cache-registered RunContext (FindAsync above returns the
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

        await engine.ReenterAsync(entry, response, ct).ConfigureAwait(false);
        return new RunResumeOutcome(true, RootRunId: entry.RunId);
    }
}
