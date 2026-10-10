using Agentivity.Domain.Execution;
using Agentivity.Host.Executions;
using Agentivity.Host.Runs;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Interactions.Chat.Services;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Contracts.Events;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Execution.Executable;

namespace Agentivity.Host.Agentic;

/// <summary>
/// Session-scoped endpoints. A session groups one or more nested runs (workflow → team → agent)
/// under a single <see cref="RunContext.ExecutionId"/>.
/// <para>
/// Routes:
///   GET  /api/v1/executions?entityId={id}             — list executions for an entity (EPIC-execution-centric-api, Story 2).
///   GET  /api/v1/executions/{executionId}             — execution detail.
///   POST /api/v1/executions/{executionId}/respond     — deliver a HIL response to any pending request within a session.
///   GET  /api/v1/executions/{executionId}/hil/pending — list all pending HIL requests for a session.
/// </para>
/// <para>
/// For standalone agents, <c>RunId == ExecutionId</c> (since Story 1), so a multiturn
/// conversation collapses into a single execution entry. For team/workflow entities a run is
/// still 1:1 with an execution. Per-run technical detail (streaming, cancel, resume) remains on
/// <c>/api/v1/runs/{runId}/...</c>.
/// </para>
/// </summary>
internal static class ExecutionsEndpoints
{
    public static IEndpointRouteBuilder MapExecutionsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var v1 = endpoints.MapV1();

        // GET /api/v1/executions?entityId={entityId}          — execution history for one entity.
        // GET /api/v1/executions?state=active&state=paused... — unscoped, by lifecycle state; backs
        //   the home page's "active & needs attention" panel. Reads real ExecutionRecords (the
        //   generic, kind-agnostic "one thing the user asked to run" concept) instead of RunRecords
        //   — unlike the entityId-scoped branch below, it never surfaces a team's internal
        //   member-agent child runs, and collapses a paused-then-resumed execution into one entry.
        v1.MapGet(
                "/executions",
                async ([FromQuery] string? entityId, [FromQuery] string[]? state, [FromServices] IRunRecordStore runRecordStore, [FromServices] IExecutionRecordStore executionStore, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(entityId))
                    {
                        var states = state ?? [];
                        var records = states.Length > 0
                            ? await executionStore.ListByStatesAsync(states, ct).ConfigureAwait(false)
                            : await executionStore.FindAllAsync(ct).ConfigureAwait(false);

                        return Results.Ok(
                            new
                            {
                                total = records.Count,
                                executions = records
                                    .OrderByDescending(r => r.UpdatedAt)
                                    .Select(r => new
                                    {
                                        executionId = r.ExecutionId,
                                        entityId = r.EntityId,
                                        entityKind = r.EntityKind,
                                        state = r.State,
                                        createdAt = r.CreatedAt,
                                        updatedAt = r.UpdatedAt,
                                    }),
                            }
                        );
                    }

                    // Case-insensitive: EntityKind is stored as an enum's ToString() (e.g. "Workflow",
                    // "Agent", "Team" — see RunRecord.EntityKind), not the lowercase literal used
                    // here — a plain `==` against "agent"/"team"/"workflow" never matched anything,
                    // so this endpoint always fell through to the empty `entityKind: null` response
                    // regardless of how many runs actually existed for the entity.
                    var ownerRuns = await runRecordStore.ListByOwnerAsync(entityId, ct).ConfigureAwait(false);
                    var agentRuns = ownerRuns.Where(r => string.Equals(r.EntityKind, "agent", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (agentRuns.Count > 0)
                    {
                        return Results.Ok(
                            new
                            {
                                entityId,
                                entityKind = "agent",
                                total = agentRuns.Count,
                                executions = ExecutionsOf(agentRuns),
                            }
                        );
                    }

                    var teamRuns = ownerRuns.Where(r => string.Equals(r.EntityKind, "team", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (teamRuns.Count > 0)
                    {
                        return Results.Ok(
                            new
                            {
                                entityId,
                                entityKind = "team",
                                total = teamRuns.Count,
                                executions = ExecutionsOf(teamRuns),
                            }
                        );
                    }

                    var workflowRuns = ownerRuns.Where(r => string.Equals(r.EntityKind, "workflow", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (workflowRuns.Count > 0)
                    {
                        return Results.Ok(
                            new
                            {
                                entityId,
                                entityKind = "workflow",
                                total = workflowRuns.Count,
                                executions = ExecutionsOf(workflowRuns),
                            }
                        );
                    }

                    return Results.Ok(
                        new
                        {
                            entityId,
                            entityKind = (string?)null,
                            total = 0,
                            executions = Array.Empty<object>(),
                        }
                    );
                }
            )
            .WithName("ListExecutionsByEntity")
            .WithSummary("List execution history for any entity (agent, team, workflow)")
            .WithTags("Executions");

        // GET /api/v1/executions/{executionId}
        v1.MapGet(
                "/executions/{executionId}",
                async (string executionId, [FromServices] IExecutionRecordStore executionStore, [FromServices] IRepository<RunContext, RunRecord> runRepository, CancellationToken ct) =>
                {
                    // Try ExecutionStore first (new — includes channel info).
                    var execution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
                    if (execution is not null)
                    {
                        return Results.Ok(
                            new
                            {
                                executionId = execution.ExecutionId,
                                entityId = execution.EntityId,
                                entityKind = execution.EntityKind,
                                state = execution.State,
                                channels = execution.Channels,
                                currentRunId = execution.CurrentRunId,
                                runIds = execution.RunIds,
                                createdAt = execution.CreatedAt,
                                updatedAt = execution.UpdatedAt,
                            }
                        );
                    }

                    // Fall through to legacy lookup (by runId).
                    var legacyFound = await runRepository.FindAsync(executionId, ct).ConfigureAwait(false);
                    var agent = legacyFound;
                    if (agent is not null && agent.Kind.ToString().ToLowerInvariant() == "agent")
                    {
                        var agentProgress = agent.Payload is { } agentPayload ? AgentProgress.FromRecord(agentPayload) : null;
                        return Results.Ok(
                            new
                            {
                                executionId = agent.RunId,
                                entityKind = "agent",
                                state = agent.Status.ToString().ToLowerInvariant(),
                                createdAt = agent.CreatedAt,
                                completedAt = agent.CompletedAt,
                                content = agentProgress?.Content,
                                error = agent.Error,
                            }
                        );
                    }

                    var team = legacyFound;
                    if (team is not null && team.Kind.ToString().ToLowerInvariant() == "team")
                    {
                        var teamProgress = team.Payload is { } teamPayload ? TeamProgress.FromRecord(teamPayload) : null;
                        return Results.Ok(
                            new
                            {
                                executionId = team.RunId,
                                entityKind = "team",
                                state = team.Status.ToString().ToLowerInvariant(),
                                createdAt = team.CreatedAt,
                                completedAt = team.CompletedAt,
                                content = teamProgress?.FinalSynthesis,
                                error = team.Error,
                            }
                        );
                    }

                    var workflow = legacyFound;
                    if (workflow is not null && workflow.Kind.ToString().ToLowerInvariant() == "workflow")
                    {
                        return Results.Ok(
                            new
                            {
                                executionId,
                                entityKind = "workflow",
                                state = workflow.Status.ToString().ToLowerInvariant(),
                                createdAt = workflow.CreatedAt,
                                completedAt = workflow.CompletedAt,
                                content = (string?)null,
                                error = workflow.Error,
                            }
                        );
                    }

                    return Results.NotFound(new { error = $"Execution '{executionId}' not found." });
                }
            )
            .WithName("GetExecution")
            .WithSummary("Get execution detail for any entity kind (agent, team, workflow)")
            .WithTags("Executions");

        // POST /api/v1/executions/{executionId}/respond
        // The client only needs (executionId, requestId, response) — no internal runId required.
        v1.MapPost(
                "/executions/{executionId}/respond",
                async (string executionId, SessionHilRespondRequest body, [FromServices] IInteractionRequestStore interactionStore, [FromServices] IInteractionBridge interactionBridge, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(body?.Response))
                        return ValidationError("response_required", "A response message is required.");

                    // Resolve the pending request by requestId (session-scoped, no runId needed).
                    var pending = body.RequestId.HasValue
                        ? await interactionStore.GetRequestAsync(body.RequestId.Value, ct).ConfigureAwait(false)
                        : interactionStore.GetPendingRequests().FirstOrDefault(r => string.Equals(r.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase) || string.Equals(r.RunId.ToString("D"), executionId, StringComparison.OrdinalIgnoreCase));

                    if (pending is null)
                        return Results.NotFound(new { error = "No pending HIL request found for this session." });

                    // Guard: ensure the request actually belongs to this session.
                    if (!string.IsNullOrWhiteSpace(pending.ExecutionId) && !string.Equals(pending.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase) && !string.Equals(pending.RunId.ToString("D"), executionId, StringComparison.OrdinalIgnoreCase))
                        return Results.NotFound(new { error = "No pending HIL request found for this session." });

                    // IInteractionBridge.ResumeAsync saves the response and resumes the run through
                    // DurableRunService (single resume path — walks to the branch root, dispatches
                    // the IResumableEngine for the run kind).
                    var response = new InteractionResponse
                    {
                        RequestId = pending.Id,
                        Outcome = InteractionOutcome.Submitted,
                        ResponseText = body.Response,
                        Channel = pending.ChannelType,
                        RespondedAt = DateTime.UtcNow,
                    };
                    var result = await interactionBridge.ResumeAsync(pending.Id, response, ct).ConfigureAwait(false);

                    if (!result.IsSuccess)
                        return Results.Problem(result.Error, statusCode: 422);

                    return Results.Ok(
                        new
                        {
                            executionId,
                            runId = pending.RunId,
                            requestId = pending.Id,
                            state = "resumed",
                        }
                    );
                }
            )
            .WithName("RespondToExecutionHil")
            .WithSummary("Deliver a human-in-the-loop response to any pending request within an execution")
            .WithTags("Executions");

        // GET /api/v1/executions/{executionId}/hil/pending
        v1.MapGet(
                "/executions/{executionId}/hil/pending",
                (string executionId, [FromServices] IInteractionRequestStore interactionStore) =>
                {
                    var pending = interactionStore.GetPendingRequests().Where(r => string.Equals(r.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase) || string.Equals(r.RunId.ToString("D"), executionId, StringComparison.OrdinalIgnoreCase)).ToList();
                    return Results.Ok(
                        new
                        {
                            executionId,
                            count = pending.Count,
                            requests = pending.Select(r => new
                            {
                                requestId = r.Id,
                                runId = r.RunId,
                                agentId = r.NodeId,
                                title = r.Title,
                                description = r.Description,
                                question = r.Query,
                                fields = r.Fields,
                                createdAt = r.CreatedAt,
                            }),
                        }
                    );
                }
            )
            .WithName("GetPendingExecutionHil")
            .WithSummary("List all pending HIL requests for an execution")
            .WithTags("Executions");

        // GET /api/v1/executions/{executionId}/threads
        v1.MapGet(
                "/executions/{executionId}/threads",
                async (string executionId, [FromServices] ChatService interactionThreadService, CancellationToken ct) =>
                {
                    var threads = await interactionThreadService.ListThreadsByExecutionAsync(executionId, ct).ConfigureAwait(false);
                    return Results.Ok(
                        threads.Select(t => new
                        {
                            threadId = t.Id,
                            runId = t.RunId,
                            executionId = t.ExecutionId,
                            title = t.Title,
                            isDefault = t.IsDefault,
                            status = t.Status.ToString(),
                            createdAt = t.CreatedAt,
                            updatedAt = t.UpdatedAt,
                        })
                    );
                }
            )
            .WithName("ListExecutionThreads")
            .WithSummary("List all interaction threads across all runs in an execution (session)")
            .WithTags("Executions");

        // GET /api/v1/executions/{executionId}/threads/{threadId}/messages
        v1.MapGet(
                "/executions/{executionId}/threads/{threadId:guid}/messages",
                async (string executionId, Guid threadId, [FromServices] ChatService interactionThreadService, CancellationToken ct) =>
                {
                    try
                    {
                        var messages = await interactionThreadService.ListMessagesByExecutionThreadAsync(executionId, threadId, ct).ConfigureAwait(false);
                        return Results.Ok(
                            messages.Select(m => new
                            {
                                messageId = m.Id,
                                threadId = m.ThreadId,
                                runId = m.RunId,
                                authorType = m.AuthorType.ToString(),
                                authorId = m.AuthorId,
                                authorName = m.AuthorName,
                                text = m.Text,
                                createdAt = m.CreatedAt,
                                updatedAt = m.UpdatedAt,
                                metadata = m.Metadata,
                            })
                        );
                    }
                    catch (KeyNotFoundException ex)
                    {
                        return Results.NotFound(new { error = ex.Message });
                    }
                }
            )
            .WithName("ListExecutionThreadMessages")
            .WithSummary("List messages in a thread, validated to belong to the execution")
            .WithTags("Executions");

        // ── POST /api/v1/executions ──────────────────────────────────────────
        // Primary entry point: start a new execution (creates a run internally).
        v1.MapPost(
                "/executions",
                async (
                    StartExecutionRequest body,
                    [FromServices] Agentivity.Host.Executions.ExecutionService executionService,
                    [FromServices] IExecutableResolver resolver,
                    [FromServices] IRunEventBus eventBus,
                    [FromServices] ITeamEngine? teamEngine,
                    [FromServices] IAgenticRepository catalogue,
                    [FromServices] WorkflowEngine? workflowEngine,
                    [FromServices] IRepository<ExecutionContext, ExecutionRecord> executionRepository,
                    [FromServices] ILoggerFactory loggerFactory,
                    [FromServices] IRepository<RunContext, RunRecord>? runRepository,
                    CancellationToken ct
                ) => await executionService.StartAsync(body, resolver, eventBus, teamEngine, catalogue, workflowEngine, executionRepository, loggerFactory, ct, runRepository)
            )
            .WithName("StartExecution")
            .WithSummary("Start a new execution (workflow, agent, or team)")
            .WithTags("Executions");

        // ── POST /api/v1/executions/{executionId}/cancel ─────────────────────
        v1.MapPost(
                "/executions/{executionId}/cancel",
                async (string executionId, [FromServices] IExecutionRecordStore executionStore, [FromServices] IRepository<RunContext, RunRecord> runRepository, CancellationToken ct) =>
                {
                    var execution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
                    if (execution is null)
                        return Results.NotFound(new { error = $"Execution '{executionId}' not found." });
                    if (!string.IsNullOrEmpty(execution.CurrentRunId))
                    {
                        var ctx = await runRepository.FindAsync(execution.CurrentRunId, ct).ConfigureAwait(false);
                        if (ctx is not null && ctx.TryCancel())
                            await runRepository.AddAsync(ctx, ct).ConfigureAwait(false);
                    }
                    await executionStore.SaveAsync(execution with { State = "cancelled", UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
                    return Results.Ok(new { executionId, state = "cancelled" });
                }
            )
            .WithName("CancelExecution")
            .WithSummary("Cancel the active run of an execution")
            .WithTags("Executions");

        // ── POST /api/v1/executions/{executionId}/pause ──────────────────────
        v1.MapPost(
                "/executions/{executionId}/pause",
                async (string executionId, [FromServices] IExecutionRecordStore executionStore, [FromServices] IRepository<RunContext, RunRecord> runRepository, [FromServices] ITeamEngine? teamEngine, CancellationToken ct) =>
                {
                    var execution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
                    if (execution is null)
                        return Results.NotFound(new { error = $"Execution '{executionId}' not found." });
                    if (!string.IsNullOrEmpty(execution.CurrentRunId))
                    {
                        var ctx = await runRepository.FindAsync(execution.CurrentRunId, ct).ConfigureAwait(false);
                        if (ctx is not null && ctx.TryPause())
                            await runRepository.AddAsync(ctx, ct).ConfigureAwait(false);
                        if (string.Equals(execution.EntityKind, "team", StringComparison.OrdinalIgnoreCase) && teamEngine is not null)
                            await teamEngine.PauseAsync(execution.CurrentRunId, ct).ConfigureAwait(false);
                    }
                    await executionStore.SaveAsync(execution with { State = "paused", UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
                    return Results.Ok(new { executionId, state = "paused" });
                }
            )
            .WithName("PauseExecution")
            .WithSummary("Pause the active run of an execution")
            .WithTags("Executions");

        // ── POST /api/v1/executions/{executionId}/unpause ────────────────────
        v1.MapPost(
                "/executions/{executionId}/unpause",
                async (string executionId, [FromServices] IExecutionRecordStore executionStore, [FromServices] IRepository<RunContext, RunRecord> runRepository, [FromServices] ITeamEngine? teamEngine, CancellationToken ct) =>
                {
                    var execution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
                    if (execution is null)
                        return Results.NotFound(new { error = $"Execution '{executionId}' not found." });
                    if (!string.IsNullOrEmpty(execution.CurrentRunId))
                    {
                        var ctx = await runRepository.FindAsync(execution.CurrentRunId, ct).ConfigureAwait(false);
                        if (ctx is not null && ctx.TryResume())
                            await runRepository.AddAsync(ctx, ct).ConfigureAwait(false);
                        if (string.Equals(execution.EntityKind, "team", StringComparison.OrdinalIgnoreCase) && teamEngine is not null)
                            await teamEngine.ResumeAsync(execution.CurrentRunId, ct).ConfigureAwait(false);
                    }
                    await executionStore.SaveAsync(execution with { State = "active", UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
                    return Results.Ok(new { executionId, state = "running" });
                }
            )
            .WithName("UnpauseExecution")
            .WithSummary("Unpause the active run of an execution")
            .WithTags("Executions");

        // ── GET /api/v1/executions/{executionId}/status ──────────────────────
        v1.MapGet(
                "/executions/{executionId}/status",
                async (string executionId, [FromServices] IExecutionRecordStore executionStore, [FromServices] IRepository<RunContext, RunRecord> runRepository, CancellationToken ct) =>
                {
                    var execution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
                    if (execution is null)
                        return Results.NotFound(new { error = $"Execution '{executionId}' not found." });
                    var entry = string.IsNullOrEmpty(execution.CurrentRunId) ? null : await runRepository.FindAsync(execution.CurrentRunId, ct).ConfigureAwait(false);
                    if (entry is not null)
                    {
                        return Results.Ok(
                            new
                            {
                                executionId,
                                runId = execution.CurrentRunId,
                                entityKind = execution.EntityKind,
                                state = entry.Status.ToString().ToLowerInvariant(),
                                nodeStatuses = Agentivity.Host.Workflows.WorkflowsEndpoints.MapNodeStatusesFromRegistry(entry),
                                connectionStatuses = Agentivity.Host.Workflows.WorkflowsEndpoints.MapConnectionStatusesFromRegistry(entry),
                            }
                        );
                    }
                    return Results.Ok(
                        new
                        {
                            executionId,
                            state = execution.State,
                            nodeStatuses = Array.Empty<object>(),
                            connectionStatuses = Array.Empty<object>(),
                        }
                    );
                }
            )
            .WithName("GetExecutionStatus")
            .WithSummary("Get live node/connection statuses for an execution")
            .WithTags("Executions");

        // ── GET /api/v1/executions/{executionId}/inspector ───────────────────
        v1.MapGet(
                "/executions/{executionId}/inspector",
                async (string executionId, [FromServices] IExecutionRecordStore executionStore, [FromServices] IRepository<RunContext, RunRecord> runRepository, CancellationToken ct) =>
                {
                    var execution = await executionStore.GetAsync(executionId, ct).ConfigureAwait(false);
                    if (execution is null)
                        return Results.NotFound(new { error = $"Execution '{executionId}' not found." });
                    if (string.IsNullOrEmpty(execution.CurrentRunId))
                        return Results.NotFound(new { error = $"Execution '{executionId}' has no active run." });
                    var snapshot = (await runRepository.FindAsync(execution.CurrentRunId, ct).ConfigureAwait(false))?.ToInspectorSnapshot();
                    if (snapshot is null)
                        return Results.NotFound(new { error = $"No inspector data for execution '{executionId}'." });
                    return Results.Ok(snapshot);
                }
            )
            .WithName("GetExecutionInspector")
            .WithSummary("Debug/inspection snapshot for an execution")
            .WithTags("Executions");

        return endpoints;
    }

    /// <summary>
    /// An entity's execution history, one entry per EXECUTION: the id every other execution endpoint (threads, messages, inspector,
    /// resume…) takes. A run is not an execution — a team's or a workflow's run has its own RunId, and an execution that was resumed
    /// or continued has several runs — so the entries are grouped by the runs' <c>ExecutionId</c>, with the state of the latest run
    /// (a run record without one is its own execution). The run's own id is returned too, as <c>runId</c>.
    /// </summary>
    internal static object[] ExecutionsOf(IEnumerable<RunRecord> runs) =>
        runs
            .GroupBy(r => string.IsNullOrWhiteSpace(r.ExecutionId) ? r.RunId : r.ExecutionId!)
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.CreatedAt).First();
                return (Latest: latest, Entry: (object)new
                {
                    executionId = g.Key,
                    runId = latest.RunId,
                    state = latest.Status.ToString().ToLowerInvariant(),
                    createdAt = g.Min(r => r.CreatedAt),
                    updatedAt = latest.CompletedAt ?? latest.CreatedAt,
                    completedAt = latest.CompletedAt,
                    error = latest.Error,
                });
            })
            .OrderByDescending(x => x.Latest.CreatedAt)
            .Select(x => x.Entry)
            .ToArray();

    private static IResult ValidationError(string code, string message) =>
        Results.BadRequest(
            new
            {
                status = "failed",
                code,
                message,
            }
        );
}

/// <summary>Session-scoped HIL response. Only requestId + response are needed — the backend resolves the run.</summary>
public sealed record SessionHilRespondRequest(string Response, Guid? RequestId = null);
