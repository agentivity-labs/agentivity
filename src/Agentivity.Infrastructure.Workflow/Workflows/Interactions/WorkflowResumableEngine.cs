using Agentivity.Infrastructure.Interactions;
using Agentivity.Infrastructure.Registry;
using Agentivity.Observability.Contracts;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Workflows.Interactions;

/// <summary>
/// <see cref="IResumableEngine"/> for workflow runs. Takes the run up again at the step it stopped on (with the person's
/// answer when there is one) and continues it in the background. <c>DurableRunService</c> decides which run: the parent of
/// the run that was waiting, then, once it has finished, its own parent. A workflow called by a team is taken up by the
/// team (<c>WorkflowMemberTask</c> owns its child's resume).
/// </summary>
public sealed class WorkflowResumableEngine(WorkflowEngine workflowEngine, ILogger<WorkflowResumableEngine>? logger = null) : IResumableEngine
{
    public string Kind => "workflow";

    // A workflow keeps its place only where it stops to wait (a person, a run it called): cut off mid-step, it is run again.
    public bool RecoversInterrupted => false;

    public async ValueTask<Task> ReenterAsync(RunContext run, InteractionResponse? response, CancellationToken ct)
    {
        if (!Guid.TryParse(run.RunId, out var runGuid))
            return Task.CompletedTask;

        // With a person's answer, or with nothing when the run waits for a run it called that has now finished: it is taken
        // up at the step it stopped on either way.
        var suspended = await workflowEngine.TakeUpWaitingRunAsync(runGuid, response, ct).ConfigureAwait(false);
        if (suspended is null)
        {
            // Not waiting: a run interrupted mid-step by a crash has no checkpoint to re-enter from (a workflow only
            // checkpoints where it stops to wait) — v1 does not replay it.
            logger?.LogWarning("Workflow run {RunId} is not waiting and cannot be taken up again (workflows checkpoint only where they stop to wait).", run.RunId);
            return Task.CompletedTask;
        }

        // A run called by another one goes on in its own place in the tree of runs: same execution, same stream.
        return workflowEngine.ResumeAfterInteractionAsync(suspended, CancellationToken.None, parentContext: run.ParentRunId is not null ? run : null).AsTask();
    }
}
