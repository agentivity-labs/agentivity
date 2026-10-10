using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Interactions;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// <see cref="IResumableEngine"/> for standalone agent runs. The agent suspended on a HIL gate
/// via <see cref="HilWaitProxy"/>; re-invoking it loads the session (marker
/// <c>[[HIL_PENDING:…]]</c>), injects the stored response and continues the ReAct loop.
/// </summary>
public sealed class StandaloneAgentResumableEngine(
    IAgenticRepository catalogue,
    StandaloneAgent standaloneAgent,
    ILogger<StandaloneAgentResumableEngine>? logger = null,
    IRepository<RunContext, RunRecord>? runRepository = null,
    IRunEventBus? eventBus = null,
    IExecutionRecordStore? executionStore = null
) : IResumableEngine
{
    public string Kind => "agent";

    public async ValueTask ReenterAsync(RunContext run, InteractionResponse? response, CancellationToken ct)
    {
        var agentId = run.EntityId;
        if (agentId is null || run.Payload is not { } payload)
        {
            logger?.LogWarning("Agent run {RunId} has no progress payload — cannot re-enter.", run.RunId);
            return;
        }
        var progress = AgentProgress.FromRecord(payload);

        var agentResult = await catalogue.GetAgentAsync(agentId, ct).ConfigureAwait(false);
        if (!agentResult.IsSuccess || agentResult.Value is null)
        {
            logger?.LogWarning("Agent '{AgentId}' of run {RunId} could not be resolved — cannot re-enter.", agentId, run.RunId);
            return;
        }

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["input"] = progress.Input };
        var executionId = run.Execution.ExecutionId;
        var streamId = run.Execution.StreamId;
        var entryNodeId = agentResult.Value.Graph.EntryNode;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var result = await standaloneAgent.RunAsync(agentResult.Value, args, run, CancellationToken.None).ConfigureAwait(false);
                    await ReportTurnOutcomeAsync(run.RunId, agentId, entryNodeId, executionId, streamId, result, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Agent HIL resume failed for run {RunId} (agent {AgentId}).", run.RunId, agentId);
                    await ReportFailureAsync(run.RunId, agentId, executionId, streamId, ex, CancellationToken.None).ConfigureAwait(false);
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>
    /// Mirrors <c>RunsService.StartAgentRunAsync</c>'s catch block: without this, a resume that
    /// throws leaves the run silently stuck — no <see cref="RunStatus.Failed"/> ever written, no
    /// <c>run_failed</c> event ever published, so the frontend has no way to learn the run is dead
    /// and stays showing it as still in progress indefinitely (the same class of bug as the missing
    /// <c>streamId</c> on <see cref="ReportTurnOutcomeAsync"/> — silent to the client either way).
    /// </summary>
    private async ValueTask ReportFailureAsync(string runId, string agentId, string executionId, string? streamId, Exception ex, CancellationToken ct)
    {
        var failedAt = DateTimeOffset.UtcNow;

        if (runRepository is not null)
        {
            var failedCtx = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);
            if (failedCtx is not null)
            {
                failedCtx.SetStatus(RunStatus.Failed, ex.Message, failedAt);
                failedCtx.AppendError(ex.Message);
                await runRepository.AddAsync(failedCtx, ct).ConfigureAwait(false);
            }
        }

        if (executionStore is not null)
        {
            var existing = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
            if (existing is not null)
                await executionStore.SaveAsync(existing with { State = "failed", UpdatedAt = failedAt }, ct).ConfigureAwait(false);
        }

        eventBus?.Publish(
            new RunEvent(
                RunId: runId,
                DefinitionId: agentId,
                RunKind: RunKind.Agent,
                Kind: RunEventKind.run_failed,
                EventName: "agent_run_failed",
                OccurredAtUtc: failedAt,
                Payload: new { error = ex.Message },
                Terminal: true,
                ExecutionId: executionId,
                StreamId: streamId
            )
        );
    }

    /// <summary>
    /// Shared with <c>RunsService.StartAgentRunAsync</c> and <c>ExecutionService.StartAgentAsync</c>
    /// via <see cref="AgentTurnOutcomeReporter"/> — see its doc comment for why this needed to stop
    /// being reimplemented independently at each call site (this one was missing entirely, which
    /// left a resumed run's status stuck at whatever the FIRST suspension left it in, with no
    /// completion signal ever reaching the frontend). Only the execution-record state update stays
    /// local: <see cref="AgentTurnOutcomeReporter"/> deliberately doesn't own that part.
    /// <para>
    /// <paramref name="streamId"/> must be forwarded: the frontend's chat/run panels subscribe to
    /// the StreamId-keyed channel (<c>/api/v1/streams/{streamId}/events</c>), not the RunId-keyed
    /// stream — a <c>run_completed</c> published without it never reaches the client that's actually
    /// listening, leaving the UI stuck showing the run as still in progress after a HIL resume
    /// (confirmed in production: input box stayed disabled, no RUN_FINISHED frame ever arrived).
    /// </para>
    /// </summary>
    private async ValueTask ReportTurnOutcomeAsync(string runId, string agentId, string? entryNodeId, string executionId, string? streamId, AgentRunResult result, CancellationToken ct)
    {
        // Node/step status must be committed BEFORE the top-level run status flips to Completed —
        // see the identical fix (and its reasoning) in RunsService.StartAgentRunAsync /
        // ExecutionService.StartAgentAsync. This resume path was missing it entirely (not just
        // racy): a run that finalizes via HIL resume never touched node status at all, leaving the
        // node frozen on whatever it showed before the resume (confirmed in production: node stuck
        // blue after a run that HTTP-level reported "completed").
        if (result.Status != ExecutableStatus.WaitingFor && runRepository is not null)
        {
            var completedCtx = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);
            completedCtx?.UpsertStep(agentId, step => { step.Status = RunStatus.Completed; step.Outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["output"] = result.Response }; });
            if (entryNodeId is not null)
                completedCtx?.UpsertStep(entryNodeId, step => step.Status = RunStatus.Completed);
            if (entryNodeId is not null)
                eventBus?.Publish(new RunEvent(RunId: runId, DefinitionId: agentId, RunKind: RunKind.Agent, Kind: RunEventKind.node_updated, EventName: "node_updated", OccurredAtUtc: DateTimeOffset.UtcNow, ExecutionId: executionId, StreamId: streamId));
        }

        var completed = await AgentTurnOutcomeReporter.ReportAsync(runRepository, eventBus, runId, agentId, executionId, result.Status, result.Response, ct, streamId).ConfigureAwait(false);

        if (executionStore is not null)
        {
            var existing = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
            if (existing is not null)
                await executionStore.SaveAsync(existing with { State = completed ? "completed" : "paused", UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        }
    }
}
