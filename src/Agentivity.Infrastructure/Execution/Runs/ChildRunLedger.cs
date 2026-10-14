using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// Keeps the run of something that was called (by a team, by a step of a workflow) in the tree of runs, and its state true.
/// <para>
/// The tree of runs is what everything else goes by: a person's answer finds the run that asked, a parent finds the run it
/// called. An engine that records its own runs (workflows, teams) needs nothing here. An agent does not record its run, so
/// whoever runs an agent as a child does it through this, the same way wherever that is.
/// </para>
/// </summary>
public static class ChildRunLedger
{
    /// <summary>Makes the run known, with its parent and the step that called it (both are carried by <paramref name="child"/>).</summary>
    public static async ValueTask RegisterAsync(IRepository<RunContext, RunRecord>? runs, RunContext child, CancellationToken ct = default)
    {
        if (runs is not null)
            await runs.AddAsync(child, ct).ConfigureAwait(false);
    }

    /// <summary>Records where the run stands. A run left "running" when it is not would be taken for one a crash interrupted.</summary>
    public static async ValueTask SetStateAsync(IRepository<RunContext, RunRecord>? runs, string runId, RunStatus state, string? error = null, CancellationToken ct = default)
    {
        if (runs is null)
            return;
        var run = await runs.FindAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return;
        run.SetStatus(state, error);
        await runs.AddAsync(run, ct).ConfigureAwait(false);
    }

    /// <summary>Records where the run stands after what it ran returned: finished, waiting, or failed.</summary>
    public static ValueTask SetOutcomeAsync(IRepository<RunContext, RunRecord>? runs, string runId, ExecutorResult result, CancellationToken ct = default) =>
        SetStateAsync(
            runs,
            runId,
            result.Status switch
            {
                ExecutableStatus.WaitingFor => RunStatus.WaitingForInput,
                ExecutableStatus.Failed => RunStatus.Failed,
                _ => RunStatus.Completed,
            },
            error: result.Status == ExecutableStatus.Failed ? result.Content : null,
            ct: ct
        );
}
