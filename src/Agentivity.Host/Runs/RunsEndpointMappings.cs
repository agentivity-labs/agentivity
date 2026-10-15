using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Domain.Run;
using Agentivity.Host.Workflows;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Interactions;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Execution.Executions;

namespace Agentivity.Host.Runs;

/// <summary>
/// Unified run dispatcher.
/// <para>
/// POST   /api/v1/runs                — starts any entity (agent | team | workflow) by entity id.
/// GET    /api/v1/runs?entityId={id}  — history of runs for a given entity (all types).
/// GET    /api/v1/runs/{runId}        — unified run status across all entity types.
/// POST   /api/v1/runs/{runId}/cancel — unified cancel across all entity types.
/// Returns a uniform envelope with runId, executionId, and streamUrl regardless of entity type.
/// The frontend uses the same API surface for agents, teams, and workflows.
/// </para>
/// </summary>
internal static class RunsEndpointMappings
{
    private const string StreamBase = "/api/v1/streams/runs";

    public static IEndpointRouteBuilder MapRunsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var v1 = endpoints.MapV1();

        // GET /api/v1/runs                          — every run, every entity kind, newest first.
        // GET /api/v1/runs?entityId={id}            — run history scoped to a single entity.
        // GET /api/v1/runs?state=running&state=...  — filter by lifecycle state (repeatable); combine with entityId to scope both.
        // Note: this lists RunRecords, an internal execution-tracking detail (e.g. a team's
        // individual member-agent turns are each their own run) — the home page's "active & needs
        // attention" panel uses GET /api/v1/executions instead, the generic one-per-user-action
        // concept.
        v1.MapGet(
                "/runs",
                async ([FromQuery] string? entityId, [FromQuery] string[]? state, [FromServices] IRunRecordStore runRecordStore, [FromServices] IExecutionRecordStore executionStore, CancellationToken ct) =>
                {
                    IReadOnlyList<RunRecord> records;
                    string? scopedEntityKind = null;

                    var states = (state ?? [])
                        .Select(s => Enum.TryParse<RunStatus>(s, ignoreCase: true, out var parsed) ? parsed : (RunStatus?)null)
                        .Where(s => s is not null)
                        .Select(s => s!.Value)
                        .Distinct()
                        .ToList();

                    if (string.IsNullOrWhiteSpace(entityId))
                    {
                        if (states.Count > 0)
                        {
                            // Scoped-by-state listing — reads only the relevant lifecycle-state
                            // partitions instead of every persisted record. Backs the home page's
                            // "active & needs attention" panel.
                            var byState = new Dictionary<string, RunRecord>(StringComparer.Ordinal);
                            foreach (var s in states)
                                foreach (var record in await runRecordStore.ListByStateAsync(s, ct).ConfigureAwait(false))
                                    byState[record.RunId] = record;
                            records = [.. byState.Values.OrderByDescending(r => r.CreatedAt)];
                        }
                        else
                        {
                            // Unscoped listing — every persisted run record, any entity kind. Backs the
                            // "Executions" screen, which shows agent/team/workflow runs together.
                            records = (await runRecordStore.FindAllAsync(ct).ConfigureAwait(false)).OrderByDescending(r => r.CreatedAt).ToList();
                        }
                    }
                    else
                    {
                        records = await runRecordStore.ListByOwnerAsync(entityId, ct).ConfigureAwait(false);
                        if (states.Count > 0)
                            records = [.. records.Where(r => states.Contains(r.Status))];
                        scopedEntityKind = records.Count > 0 ? records[0].EntityKind : null;
                    }

                    // Resolve each run's StreamId via its ExecutionRecord — needed by the frontend to
                    // reconnect to the run's SSE channel (chat, live status) after navigating away and
                    // back, since streamId is a distinct GUID from runId/executionId.
                    var streamIds = new Dictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var executionId in records.Select(r => r.ExecutionId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
                    {
                        var execution = await executionStore.GetAsync(executionId!, ct).ConfigureAwait(false);
                        streamIds[executionId!] = execution?.StreamId;
                    }

                    return Results.Ok(
                        new
                        {
                            entityId,
                            entityKind = scopedEntityKind,
                            total = records.Count,
                            runs = records.Select(r => new
                            {
                                runId = r.RunId,
                                entityId = r.EntityId,
                                entityKind = r.EntityKind,
                                executionId = r.ExecutionId,
                                streamId = r.ExecutionId is not null ? streamIds.GetValueOrDefault(r.ExecutionId) : null,
                                state = r.Status.ToString().ToLowerInvariant(),
                                createdAt = r.CreatedAt,
                                completedAt = r.CompletedAt,
                                error = r.Error,
                            }),
                        }
                    );
                }
            )
            .WithName("ListRuns")
            .WithSummary("List runs — unscoped (all entity kinds) or scoped to a single entity via entityId")
            .WithTags("Runs");

        // GET /api/v1/runs/{runId}
        // Resolves run status regardless of entity type by trying each store in order.
        v1.MapGet(
                "/runs/{runId}",
                async (string runId, [FromServices] IRepository<RunContext, RunRecord>? runRepository) =>
                {
                    var found = runRepository is null ? null : await runRepository.FindAsync(runId).ConfigureAwait(false);

                    // Agent (durable RunRecord + AgentProgress payload)
                    var agent = found;
                    if (agent is not null && agent.Kind.ToString().ToLowerInvariant() == "agent")
                    {
                        var agentProgress = agent.Payload is { } agentPayload ? AgentProgress.FromRecord(agentPayload) : null;
                        return Results.Ok(
                            new
                            {
                                runId = agent.RunId,
                                entityKind = "agent",
                                state = agent.Status.ToString().ToLowerInvariant(),
                                createdAt = agent.CreatedAt,
                                completedAt = agent.CompletedAt,
                                content = agentProgress?.Content,
                                error = agent.Error,
                            }
                        );
                    }

                    // Team (durable RunRecord + TeamProgress payload)
                    var team = found;
                    if (team is not null && team.Kind.ToString().ToLowerInvariant() == "team")
                    {
                        var teamProgress = team.Payload is { } teamPayload ? TeamProgress.FromRecord(teamPayload) : null;
                        return Results.Ok(
                            new
                            {
                                runId = team.RunId,
                                entityKind = "team",
                                state = team.Status.ToString().ToLowerInvariant(),
                                createdAt = team.CreatedAt,
                                completedAt = team.CompletedAt,
                                content = teamProgress?.FinalSynthesis,
                                error = team.Error,
                            }
                        );
                    }

                    // Workflow (durable RunRecord)
                    var workflow = found;
                    if (workflow is not null && workflow.Kind.ToString().ToLowerInvariant() == "workflow")
                    {
                        return Results.Ok(
                            new
                            {
                                runId,
                                entityKind = "workflow",
                                state = workflow.Status.ToString().ToLowerInvariant(),
                                createdAt = workflow.CreatedAt,
                                completedAt = workflow.CompletedAt,
                                content = (string?)null,
                                error = workflow.Error,
                            }
                        );
                    }

                    return Results.NotFound(new { error = $"Run '{runId}' not found." });
                }
            )
            .WithName("GetUnifiedRun")
            .WithSummary("Get run status for any entity kind (agent, team, workflow)")
            .WithTags("Runs");

        // GET /api/v1/runs/{runId}/status
        // Returns unified node/connection statuses for any entity kind — the single polling endpoint for Flutter.
        v1.MapGet(
                "/runs/{runId}/status",
                async (
                    string runId,
                    [FromServices] ITeamTopologyPositionStatusRegistry? teamTopologyPositionRegistry,
                    [FromServices] IRepository<RunContext, RunRecord>? runRepository
                ) =>
                {
                    var statusFound = runRepository is null ? null : await runRepository.FindAsync(runId).ConfigureAwait(false);

                    // ── Agent ─────────────────────────────────────────────────────────────
                    var agentEntry = statusFound;
                    if (agentEntry is not null && agentEntry.Kind.ToString().ToLowerInvariant() == "agent")
                    {
                        var agentState = agentEntry.Status.ToString().ToLowerInvariant();
                        return Results.Ok(
                            new
                            {
                                runId = agentEntry.RunId,
                                entityKind = "agent",
                                state = agentState,
                                error = agentEntry.Error,
                                createdAt = agentEntry.CreatedAt,
                                completedAt = agentEntry.CompletedAt?.DateTime,
                                nodeStatuses = WorkflowsEndpoints.MapNodeStatusesFromRegistry(agentEntry),
                                connectionStatuses = Array.Empty<object>(),
                            }
                        );
                    }

                    // ── Team ──────────────────────────────────────────────────────────────
                    var team = statusFound;
                    if (team is not null && team.Kind.ToString().ToLowerInvariant() == "team")
                    {
                        var topologyPositionStatuses = teamTopologyPositionRegistry?.GetStatuses(runId) ?? new Dictionary<string, TeamTopologyPositionRunStatus>();
                        return Results.Ok(
                            new
                            {
                                runId = team.RunId,
                                entityKind = "team",
                                state = team.Status.ToString().ToLowerInvariant(),
                                error = team.Error,
                                createdAt = team.CreatedAt,
                                completedAt = team.CompletedAt,
                                nodeStatuses = topologyPositionStatuses.Values.Select(s => new
                                {
                                    nodeId = s.TopologyPositionId,
                                    state = s.State,
                                    error = s.Error,
                                }),
                                connectionStatuses = Array.Empty<object>(),
                            }
                        );
                    }

                    // ── Workflow ──────────────────────────────────────────────────────────
                    var workflowEntry = statusFound;
                    if (workflowEntry is not null && workflowEntry.Kind.ToString().ToLowerInvariant() == "workflow")
                    {
                        return Results.Ok(
                            new
                            {
                                runId,
                                entityKind = "workflow",
                                state = workflowEntry.Status.ToString().ToLowerInvariant(),
                                error = workflowEntry.Error,
                                createdAt = workflowEntry.CreatedAt.DateTime,
                                completedAt = workflowEntry.CompletedAt?.DateTime,
                                nodeStatuses = WorkflowsEndpoints.MapNodeStatusesFromRegistry(workflowEntry),
                                connectionStatuses = WorkflowsEndpoints.MapConnectionStatusesFromRegistry(workflowEntry),
                            }
                        );
                    }

                    return Results.NotFound(new { error = $"Run '{runId}' not found." });
                }
            )
            .WithName("GetUnifiedRunStatus")
            .WithSummary("Get live node/connection statuses for any entity kind (agent, team, workflow)")
            .WithTags("Runs");

        // POST /api/v1/runs/{runId}/cancel
        // Cancels any run type via the unified IRunRepository.
        v1.MapPost(
                "/runs/{runId}/cancel",
                async (string runId, [FromServices] IRepository<RunContext, RunRecord> runRepository, [FromServices] IRunEventBus eventBus) =>
                {
                    var entry = await runRepository.FindAsync(runId).ConfigureAwait(false);
                    if (entry is null)
                        return Results.NotFound(new { error = $"Run '{runId}' not found." });

                    if (entry.TryCancel())
                        await runRepository.AddAsync(entry).ConfigureAwait(false);

                    var runKind = entry.Kind;
                    eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: entry.Kind.ToString().ToLowerInvariant(), RunKind: runKind, Kind: RunEventKind.run_cancelled, EventName: "run_cancelled", OccurredAtUtc: DateTimeOffset.UtcNow, Terminal: true, ExecutionId: entry.Execution.ExecutionId));
                    return Results.Ok(new { runId, state = "cancelled" });
                }
            )
            .WithName("CancelUnifiedRun")
            .WithSummary("Cancel a run for any entity kind (agent, team, workflow)")
            .WithTags("Runs");

        // DELETE /api/v1/runs/{runId}
        v1.MapDelete(
                "/runs/{runId}",
                async (string runId, [FromServices] IRepository<RunContext, RunRecord> runRepository, [FromServices] InteractionStore interactionStore) =>
                {
                    var entry = await runRepository.FindAsync(runId).ConfigureAwait(false);
                    if (entry is null)
                        return Results.NotFound(new { error = $"Run '{runId}' not found." });

                    if (entry.Status is RunStatus.Running or RunStatus.Paused or RunStatus.WaitingForInput)
                        return ApiConflictError(code: "run_active", userMessage: "This run is still active. Cancel it first.", detail: $"Cannot delete run '{runId}' in state '{entry.Status}'.", context: new { runId, state = entry.Status.ToString() });

                    // Generic HIL cleanup
                    if (Guid.TryParse(runId, out var runGuid))
                    {
                        foreach (var req in interactionStore.GetPendingRequestsForRun(runGuid))
                            interactionStore.RemoveRequest(req.Id);
                    }

                    // DeleteAsync already deletes the durable RunRecord for every entity kind.
                    await runRepository.DeleteAsync(runId).ConfigureAwait(false);

                    return Results.Ok(new { runId, status = "deleted" });
                }
            )
            .WithName("DeleteUnifiedRun")
            .WithSummary("Delete a run for any entity kind (agent, team, workflow)")
            .WithTags("Runs");

        // POST /api/v1/runs/{runId}/pause
        v1.MapPost(
                "/runs/{runId}/pause",
                async (string runId, [FromServices] IRepository<RunContext, RunRecord> runRepository, [FromServices] ITeamEngine? teamEngine, CancellationToken ct) =>
                {
                    var entry = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);
                    if (entry is null)
                        return Results.NotFound(new { error = $"Run '{runId}' not found." });

                    if (!entry.TryPause())
                        return ApiConflictError(code: "run_pause_conflict", userMessage: "This run cannot be paused in its current state.", detail: $"Cannot pause run '{runId}' in state '{entry.Status}'.", context: new { runId, state = entry.Status.ToString() });
                    await runRepository.AddAsync(entry, ct).ConfigureAwait(false);

                    // For team runs, also pause the team engine's graph host.
                    if (entry.Kind == RunKind.Team && teamEngine is not null)
                        await teamEngine.PauseAsync(runId, ct).ConfigureAwait(false);

                    return Results.Ok(new { runId, state = "paused" });
                }
            )
            .WithName("PauseUnifiedRun")
            .WithSummary("Pause a run for any entity kind")
            .WithTags("Runs");

        // POST /api/v1/runs/{runId}/unpause
        // A run that waits for a person is taken up by answering its question, at the level of its execution
        // (POST /api/v1/executions/{executionId}/respond), not here.
        v1.MapPost(
                "/runs/{runId}/unpause",
                async (string runId, [FromServices] IRepository<RunContext, RunRecord> runRepository, [FromServices] ITeamEngine? teamEngine, CancellationToken ct) =>
                {
                    var entry = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);
                    if (entry is null)
                        return Results.NotFound(new { error = $"Run '{runId}' not found." });

                    if (!entry.TryResume())
                        return ApiConflictError(code: "run_unpause_conflict", userMessage: "This run cannot be unpaused in its current state.", detail: $"Cannot unpause run '{runId}' in state '{entry.Status}'.", context: new { runId, state = entry.Status.ToString() });
                    await runRepository.AddAsync(entry, ct).ConfigureAwait(false);

                    // For team runs, also resume the team engine's graph host.
                    if (entry.Kind == RunKind.Team && teamEngine is not null)
                        await teamEngine.ResumeAsync(runId, ct).ConfigureAwait(false);

                    return Results.Ok(new { runId, state = "running" });
                }
            )
            .WithName("UnpauseUnifiedRun")
            .WithSummary("Unpause a paused run for any entity kind")
            .WithTags("Runs");

        // POST /api/v1/runs/{runId}/recover
        // Manually re-enters a run marked Interrupted by the startup recovery (crash/restart
        // orphan). Re-entry starts from the last checkpoint — completed work is not re-executed.
        v1.MapPost(
                "/runs/{runId:guid}/recover",
                async (Guid runId, [FromServices] DurableRunService durableRunService, CancellationToken ct) =>
                {
                    var outcome = await durableRunService.RecoverAsync(runId, ct).ConfigureAwait(false);
                    return outcome.Success
                        ? Results.Ok(new { runId = outcome.RootRunId, state = "recovering" })
                        : Results.Problem(outcome.Error, statusCode: 409);
                }
            )
            .WithName("RecoverRun")
            .WithSummary("Re-enter an interrupted run from its last checkpoint")
            .WithTags("Runs");

        // GET /api/v1/runs/{runId}/entities/traces
        v1.MapGet(
                "/runs/{runId}/entities/traces",
                async (string runId, [FromServices] IRepository<RunContext, RunRecord> runRepository) =>
                {
                    var entry = await runRepository.FindAsync(runId).ConfigureAwait(false);
                    if (entry is null)
                        return Results.NotFound(new { error = $"Run '{runId}' not found." });

                    var items = entry
                        .Steps.Values.Where(s => s.Trace is not null)
                        .Select(s => new
                        {
                            entityId = s.Id,
                            state = s.Status.ToString(),
                            error = s.Error,
                            trace = s.Trace,
                        })
                        .ToList();

                    return Results.Ok(
                        new
                        {
                            runId,
                            totalCount = items.Count,
                            items,
                        }
                    );
                }
            )
            .WithName("GetRunEntityTraces")
            .WithSummary("Get all step/entity traces for a run")
            .WithTags("Runs");

        // GET /api/v1/runs/{runId}/entities/{entityId}/trace
        v1.MapGet(
                "/runs/{runId}/entities/{entityId}/trace",
                async (string runId, string entityId, [FromServices] IRepository<RunContext, RunRecord> runRepository) =>
                {
                    var trace = (await runRepository.FindAsync(runId).ConfigureAwait(false))?.GetStepTrace(entityId);
                    if (trace is null)
                        return Results.NotFound(new { error = $"No trace found for entity '{entityId}' in run '{runId}'." });
                    return Results.Ok(trace);
                }
            )
            .WithName("GetRunEntityTrace")
            .WithSummary("Get the trace for a specific step/entity in a run")
            .WithTags("Runs");

        return endpoints;
    }
}
