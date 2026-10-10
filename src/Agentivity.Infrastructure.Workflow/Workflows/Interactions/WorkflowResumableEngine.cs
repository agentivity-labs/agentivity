using Agentivity.Infrastructure.Interactions;
using Agentivity.Infrastructure.Registry;
using Agentivity.Observability.Contracts;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Workflows.Interactions;

/// <summary>
/// <see cref="IResumableEngine"/> for root workflow runs. Injects the human response into the
/// suspended state and continues the run fire-and-forget. Nested workflow runs never reach this
/// engine: <c>DurableRunService</c> walks the persisted run tree to the branch root, so a
/// workflow suspended inside a team resumes through the TEAM engine's re-entry cascade
/// (<c>WorkflowMemberTask</c> owns its child's resume).
/// </summary>
public sealed class WorkflowResumableEngine(WorkflowEngine workflowEngine, ILogger<WorkflowResumableEngine>? logger = null) : IResumableEngine
{
    public string Kind => "workflow";

    public async ValueTask ReenterAsync(RunContext run, InteractionResponse? response, CancellationToken ct)
    {
        if (!Guid.TryParse(run.RunId, out var runGuid))
            return;

        if (response is null)
        {
            // Crash recovery: a workflow only checkpoints at HIL suspension. A run that was
            // WaitingForInput at crash time is re-armed (never Interrupted); a run interrupted
            // mid-node has no checkpoint to re-enter from — v1 does not replay it.
            logger?.LogWarning("Workflow run {RunId} cannot be recovered without a checkpoint (workflows checkpoint at HIL suspension only).", run.RunId);
            return;
        }

        var suspended = await workflowEngine.ProvideInteractionResponseAsync(runGuid, response, ct).ConfigureAwait(false);
        if (suspended is not null)
            _ = workflowEngine.ResumeAfterInteractionAsync(suspended, CancellationToken.None);
    }
}
