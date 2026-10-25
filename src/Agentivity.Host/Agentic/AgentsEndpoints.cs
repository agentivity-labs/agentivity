using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Storage.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Host.Agentic;

/// <summary>
/// CRUD endpoints for agents.
/// Routes: /api/v1/agentic/agents
/// Starting:     POST /api/v1/executions  |  GET /api/v1/runs?entityId=  |  GET /api/v1/runs/{runId}
/// Answering:    POST /api/v1/executions/{executionId}/respond
/// </summary>
internal static class AgentsEndpoints
{
    private const string Base = "/api/v1/agentic/agents";

    public static IEndpointRouteBuilder MapAgentsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapV1().MapGroup("/agentic/agents").WithTags("Agents");

        MapAgentCrud(group);

        return endpoints;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    private static void MapAgentCrud(RouteGroupBuilder g)
    {
        g.MapGet(
                "",
                async ([FromQuery] string? folderId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var agents = await repo.ListAgentsAsync(folderId, ct).ConfigureAwait(false);
                    return Results.Ok(agents.Select(AgenticMapper.ToSummary));
                }
            )
            .WithName("ListAgents")
            .WithSummary("List all agents in the library");

        g.MapGet(
                "/{agentId}",
                async (string agentId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.GetAgentAsync(agentId, ct).ConfigureAwait(false);
                    return result.IsSuccess ? Results.Ok(result.Value!) : Results.NotFound(new { error = result.Error });
                }
            )
            .WithName("GetAgent")
            .WithSummary("Get an agent by ID");

        g.MapPost(
                "",
                async (SaveAgentRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var entry = AgenticMapper.ToAgent(body);
                    var result = await repo.SaveAgentAsync(entry, cancellationToken: ct).ConfigureAwait(false);
                    return result.IsSuccess ? Results.Created($"{Base}/{result.Value!.Id}", result.Value!) : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("CreateAgent")
            .WithSummary("Create a new agent");

        g.MapPut(
                "/{agentId}",
                async (string agentId, SaveAgentRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var entry = AgenticMapper.ToAgent(body with { Id = agentId });
                    var result = await repo.SaveAgentAsync(entry, cancellationToken: ct).ConfigureAwait(false);
                    return result.IsSuccess ? Results.Ok(result.Value!) : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("UpdateAgent")
            .WithSummary("Update an existing agent");

        g.MapDelete(
                "/{agentId}",
                async (string agentId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.DeleteAgentAsync(agentId, ct).ConfigureAwait(false);
                    return result.IsSuccess ? Results.NoContent() : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("DeleteAgent")
            .WithSummary("Delete an agent");

        g.MapPost(
                "/{agentId}/move",
                async (string agentId, MoveItemRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.MoveAgentAsync(agentId, body.TargetFolderId, ct).ConfigureAwait(false);
                    return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("MoveAgent")
            .WithSummary("Move an agent to a folder");

        // ── Versioning (requires workspace.versioning module) ──────────────────

        g.MapGet(
                "/{agentId}/versions",
                async (string agentId, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
                {
                    if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                        return Results.NotFound();
                    var versions = await versionStore.ListVersionsAsync("agent", agentId, ct).ConfigureAwait(false);
                    return Results.Ok(versions);
                }
            )
            .WithName("ListAgentVersions")
            .WithSummary("List saved versions of an agent");

        g.MapPost(
                "/{agentId}/versions",
                async (string agentId, CreateVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
                {
                    if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                        return Results.NotFound();
                    var result = await versionStore.CreateVersionAsync("agent", agentId, new WorkspaceObjectVersionCreateRequest(body.Label, body.CreatedBy), ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithName("CreateAgentVersion")
            .WithSummary("Create a version snapshot of an agent");

        g.MapGet(
                "/{agentId}/versions/{versionId}",
                async (string agentId, string versionId, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
                {
                    if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                        return Results.NotFound();
                    var result = await versionStore.GetVersionAsync("agent", agentId, versionId, ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.NotFound(new { error = result.Error });
                }
            )
            .WithName("GetAgentVersionDetail")
            .WithSummary("Get a version snapshot of an agent");

        g.MapPost(
                "/{agentId}/versions/{versionId}/restore",
                async (string agentId, string versionId, RestoreVersionRequest body, [FromServices] IWorkspaceObjectVersionStore? versionStore, CancellationToken ct) =>
                {
                    if (versionStore is null || !versionStore.VersioningCapabilities.Enabled)
                        return Results.NotFound();
                    var result = await versionStore.RestoreVersionAsync("agent", agentId, versionId, new WorkspaceObjectVersionRestoreRequest(body.Label, body.RestoredBy), ct).ConfigureAwait(false);
                    return result.Success && result.Value is not null ? Results.Ok(result.Value) : Results.BadRequest(new { error = result.Error });
                }
            )
            .WithName("RestoreAgentVersion")
            .WithSummary("Restore an agent to a saved version snapshot");
    }
}
