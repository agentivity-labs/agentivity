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
                                content = teamProgress is null ? null : Agentivity.Infrastructure.Agentic.Team.TeamAnswer.Of(teamProgress),
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
        // The one way to answer a question of an execution, whatever the channel it was asked through: the answer names
        // its question (several can wait at once), and the backend finds the run that asked it. No runId is needed.
        v1.MapPost(
                "/executions/{executionId}/respond",
                async (string executionId, SessionHilRespondRequest body, [FromServices] IInteractionRequestStore interactionStore, [FromServices] ChatService chatService, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(body?.Response))
                        return ApiEndpointHelpers.ApiValidationError(code: "response_required", userMessage: "A response is required.", detail: "The 'response' field must not be empty.");

                    if (body.RequestId is not Guid requestId)
                        return ApiEndpointHelpers.ApiValidationError(code: "request_id_required", userMessage: "Say which question this answers: a requestId is required.", detail: "The 'requestId' field must name a question of the execution (see /executions/{executionId}/hil/pending).");

                    var question = await interactionStore.GetRequestAsync(requestId, ct).ConfigureAwait(false);
                    if (question is null || !BelongsTo(question, executionId))
                        return ApiEndpointHelpers.ApiNotFound(code: "question_not_found", userMessage: "This question was not found in this execution.", detail: $"Request '{requestId}' is not a question of execution '{executionId}'.");

                    if (await interactionStore.GetResponseAsync(requestId, ct).ConfigureAwait(false) is not null)
                        return ApiEndpointHelpers.ApiConflictError(code: "already_answered", userMessage: "This question has already been answered.", detail: $"Request '{requestId}' already has a response.");

                    var answer = new ChatMessageRequest(Text: body.Response, AuthorType: "user", AuthorId: body.AuthorId, AuthorName: body.AuthorName, Metadata: string.Equals(body.Source, "widget", StringComparison.OrdinalIgnoreCase) ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["interaction.source"] = "widget" } : null, Outcome: body.Outcome);
                    var result = await chatService.AnswerAsync(question, answer, ct).ConfigureAwait(false);

                    if (!result.Resumed.IsSuccess)
                        return Results.Problem(result.Resumed.Error, statusCode: 422);

                    return Results.Ok(
                        new
                        {
                            executionId,
                            runId = question.RunId,
                            requestId = question.Id,
                            threadId = result.Thread.Id,
                            messageId = result.Message.Id,
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

        // POST /api/v1/executions/{executionId}/threads/{threadId}/messages
        // A person writes in the conversation of an execution. To answer a question, /respond names it.
        v1.MapPost(
                "/executions/{executionId}/threads/{threadId:guid}/messages",
                async (string executionId, Guid threadId, ExecutionMessageRequest body, [FromServices] ChatService chatService, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(body?.Text))
                        return ApiEndpointHelpers.ApiValidationError(code: "text_required", userMessage: "A message is required.", detail: "The 'text' field must not be empty.");

                    var thread = await chatService.GetThreadAsync(threadId, ct).ConfigureAwait(false);
                    if (thread is null || !string.Equals(thread.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase))
                        return ApiEndpointHelpers.ApiNotFound(code: "thread_not_found", userMessage: "This conversation was not found in this execution.", detail: $"Thread '{threadId}' is not a conversation of execution '{executionId}'.");

                    var result = await chatService.PostMessageAsync(threadId, new ChatMessageRequest(Text: body.Text, AuthorType: "user", AuthorId: body.AuthorId, AuthorName: body.AuthorName, Metadata: null), ct).ConfigureAwait(false);
                    return Results.Ok(
                        new
                        {
                            executionId,
                            threadId = result.Thread.Id,
                            messageId = result.Message.Id,
                            interactionRequestId = result.Interaction?.RequestId,
                        }
                    );
                }
            )
            .WithName("PostExecutionThreadMessage")
            .WithSummary("Write a message in a conversation of an execution")
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

    /// <summary>
    /// A question is one of an execution when it was asked in it (a standalone agent's run has the id of its execution). A
    /// question that does not say its execution is taken as one of it, as it always was.
    /// </summary>
    private static bool BelongsTo(InteractionRequest question, string executionId) =>
        string.IsNullOrWhiteSpace(question.ExecutionId)
        || string.Equals(question.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(question.RunId.ToString("D"), executionId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The answer to one question of an execution. <paramref name="RequestId"/> names the question; the backend finds the run that asked it.</summary>
/// <param name="Source"><c>"widget"</c> when the answer was given through a widget (it shows the answer itself, so the conversation does not show it again).</param>
/// <param name="Outcome">For an approval: <c>"approved"</c>, <c>"rejected"</c>, <c>"revised"</c>…</param>
public sealed record SessionHilRespondRequest(string Response, [property: System.Text.Json.Serialization.JsonConverter(typeof(LenientGuidConverter))] Guid? RequestId = null, string? Source = null, string? Outcome = null, string? AuthorId = null, string? AuthorName = null);

/// <summary>A message written in a conversation of an execution.</summary>
public sealed record ExecutionMessageRequest(string Text, string? AuthorId = null, string? AuthorName = null);
