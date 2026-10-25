using System.Collections.Concurrent;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Storage.Workspace;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Host.Agentic;

/// <summary>
/// CRUD endpoints for teams, plus the remaining run-status/cancel endpoints.
/// Routes: /api/v1/agentic/teams, /api/v1/agentic/teams/{teamId}/runs/{runId}[/cancel]
/// Stream: /api/v1/streams/runs/{runId}/events (unified, with replay)
/// Starting a team run goes through the unified POST /api/v1/executions
/// (<see cref="Agentivity.Host.Executions.ExecutionService"/>), not a route in this file.
/// </summary>
internal static class TeamsEndpoints
{
    private const string Base = "/api/v1/agentic/teams";
    private const string StreamBase = "/api/v1/streams/runs";

    // In-process active run cache — supplemented by durable persistence in IRunRepository/RunRecord.
    private static readonly ConcurrentDictionary<string, (string TeamId, DateTimeOffset CreatedAt)> _activeRuns = new(StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapTeamsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapV1().MapGroup("/agentic/teams").WithTags("Teams");

        MapTeamCrud(group);
        MapTeamRuns(endpoints);

        return endpoints;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    private static void MapTeamCrud(RouteGroupBuilder g)
    {
        g.MapGet(
                "",
                async ([FromQuery] string? folderId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var teams = await repo.ListTeamsAsync(folderId, ct).ConfigureAwait(false);
                    return Results.Ok(teams.Select(AgenticMapper.ToSummary));
                }
            )
            .WithName("ListTeams")
            .WithSummary("List all teams in the library");

        g.MapGet(
                "/{teamId}",
                async (string teamId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.GetTeamAsync(teamId, ct).ConfigureAwait(false);
                    return result.IsSuccess ? Results.Ok(result.Value!) : Results.NotFound(new { error = result.Error });
                }
            )
            .WithName("GetTeam")
            .WithSummary("Get a team by ID");

        g.MapPost(
                "",
                async (SaveTeamRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var entry = AgenticMapper.ToTeam(body);
                    var result = await repo.SaveTeamAsync(entry, cancellationToken: ct).ConfigureAwait(false);
                    return result.IsSuccess ? Results.Created($"{Base}/{result.Value!.Id}", result.Value!) : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("CreateTeam")
            .WithSummary("Create a new team");

        g.MapPut(
                "/{teamId}",
                async (string teamId, SaveTeamRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var entry = AgenticMapper.ToTeam(body with { Id = teamId });
                    var result = await repo.SaveTeamAsync(entry, cancellationToken: ct).ConfigureAwait(false);
                    return result.IsSuccess ? Results.Ok(result.Value!) : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("UpdateTeam")
            .WithSummary("Update an existing team");

        g.MapDelete(
                "/{teamId}",
                async (string teamId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.DeleteTeamAsync(teamId, ct).ConfigureAwait(false);
                    return result.IsSuccess ? Results.NoContent() : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("DeleteTeam")
            .WithSummary("Delete a team");

        g.MapPost(
                "/{teamId}/move",
                async (string teamId, MoveItemRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.MoveTeamAsync(teamId, body.TargetFolderId, ct).ConfigureAwait(false);
                    return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("MoveTeam")
            .WithSummary("Move a team to a folder");

        // ── Versioning (requires workspace.versioning module) ──────────────────

        g.MapGet(
                "/{teamId}/versions",
                async (string teamId, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
                {
                    if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                        return Results.NotFound();
                    var versions = await versionStore.ListVersionsAsync("team", teamId, ct).ConfigureAwait(false);
                    return Results.Ok(versions);
                }
            )
            .WithName("ListTeamVersions")
            .WithSummary("List saved versions of a team");

        g.MapPost(
                "/{teamId}/versions",
                async (string teamId, CreateVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
                {
                    if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                        return Results.NotFound();
                    var result = await versionStore.CreateVersionAsync("team", teamId, new WorkspaceObjectVersionCreateRequest(body.Label, body.CreatedBy), ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithName("CreateTeamVersion")
            .WithSummary("Create a version snapshot of a team");

        g.MapGet(
                "/{teamId}/versions/{versionId}",
                async (string teamId, string versionId, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
                {
                    if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                        return Results.NotFound();
                    var result = await versionStore.GetVersionAsync("team", teamId, versionId, ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
                }
            )
            .WithName("GetTeamVersionDetail")
            .WithSummary("Get a version snapshot of a team");

        g.MapPost(
                "/{teamId}/versions/{versionId}/restore",
                async (string teamId, string versionId, RestoreVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
                {
                    if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                        return Results.NotFound();
                    var result = await versionStore.RestoreVersionAsync("team", teamId, versionId, new WorkspaceObjectVersionRestoreRequest(body.Label, body.RestoredBy), ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithName("RestoreTeamVersion")
            .WithSummary("Restore a team to a saved version snapshot");
    }

    // ── Runs ──────────────────────────────────────────────────────────────────

    private static void MapTeamRuns(IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapV1();

        // Starting a team run goes through the unified POST /api/v1/executions
        // (Agentivity.Host.Executions.ExecutionService) — the dedicated
        // POST/GET-list /agentic/teams/{teamId}/runs routes that used to live here predated that
        // unification and had no remaining caller, so they were removed rather than kept as
        // dead/duplicate surface. GET-single-run and cancel below are still used by the Studio
        // frontend (TeamRepository.getTeamRun/cancelTeamRun) and stay as-is.

        // GET /api/v1/agentic/teams/{teamId}/runs/{runId}
        v1.MapGet(
                "/agentic/teams/{teamId}/runs/{runId}",
                async (string teamId, string runId, [FromServices] IRepository<RunContext, RunRecord> runRepository, [FromServices] ITeamTopologyPositionStatusRegistry topologyPositionStatusRegistry) =>
                {
                    var entry = await runRepository.FindAsync(runId).ConfigureAwait(false);
                    if (entry is not null)
                    {
                        var progress = entry.Payload is { } payload ? TeamProgress.FromRecord(payload) : null;
                        return Results.Ok(
                            new
                            {
                                runId = entry.RunId,
                                teamId = entry.EntityId,
                                orchestratorId = progress?.OrchestratorId,
                                state = entry.Status.ToString(),
                                outputs = (progress?.Outputs ?? []).Select(o => new
                                {
                                    taskId = o.TaskId,
                                    agentId = o.AgentId,
                                    content = o.Content,
                                    occurredAt = o.OccurredAt,
                                }),
                                topologyPositionStatuses = MapTopologyPositionStatuses(topologyPositionStatusRegistry.GetStatuses(runId)),
                                finalSynthesis = progress?.FinalSynthesis,
                                answer = progress is null ? null : Agentivity.Infrastructure.Agentic.Team.TeamAnswer.Of(progress),
                                error = entry.Error,
                                createdAt = entry.CreatedAt,
                                startedAt = entry.CreatedAt,
                                completedAt = entry.CompletedAt,
                            }
                        );
                    }

                    if (_activeRuns.TryGetValue(runId, out var active))
                    {
                        return Results.Ok(
                            new
                            {
                                runId,
                                teamId = active.TeamId,
                                state = "running",
                                outputs = Array.Empty<object>(),
                                topologyPositionStatuses = MapTopologyPositionStatuses(topologyPositionStatusRegistry.GetStatuses(runId)),
                                createdAt = active.CreatedAt,
                            }
                        );
                    }

                    return Results.NotFound(new { error = $"Run '{runId}' not found." });
                }
            )
            .WithName("GetTeamRun")
            .WithSummary("Get team run status")
            .WithTags("Teams");

        // POST /api/v1/agentic/teams/{teamId}/runs/{runId}/cancel
        v1.MapPost(
                "/agentic/teams/{teamId}/runs/{runId}/cancel",
                async (string teamId, string runId, [FromServices] ITeamEngine? engine, [FromServices] IRunEventBus eventBus, CancellationToken ct) =>
                {
                    if (engine is null)
                        return Results.Problem("The agentic engine module is not loaded.", statusCode: 503);

                    await engine.CancelAsync(runId, ct).ConfigureAwait(false);

                    // TryCancelRun (called by engine.CancelAsync) already writes State=Cancelled
                    // through to the durable RunRecord — nothing engine-specific left to persist here.
                    var cancelledAt = DateTimeOffset.UtcNow;
                    _activeRuns.TryRemove(runId, out _);

                    eventBus.Publish(new RunEvent(RunId: runId, DefinitionId: teamId, RunKind: RunKind.Team, Kind: RunEventKind.run_cancelled, EventName: "team_run_cancelled", OccurredAtUtc: cancelledAt, Terminal: true));

                    return Results.Ok(new { runId, state = "cancelled" });
                }
            )
            .WithName("CancelTeamRun")
            .WithSummary("Cancel a running team run")
            .WithTags("Teams");

        // Team HIL responses go through the generic execution-scoped respond endpoint
        // (AgentivityStudioClient.submitHilResponse / TeamRepository.submitHilResponse) — the
        // team-specific .../respond route that used to live here had no remaining caller.
    }

    private static IEnumerable<object> MapTopologyPositionStatuses(IReadOnlyDictionary<string, TeamTopologyPositionRunStatus> statuses) =>
        statuses.Values.Select(s => new
        {
            topologyPositionId = s.TopologyPositionId,
            state = s.State,
            error = s.Error,
            occurredAt = s.OccurredAtUtc,
        });
}
