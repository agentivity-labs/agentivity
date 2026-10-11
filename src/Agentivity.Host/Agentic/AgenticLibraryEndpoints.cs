using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Host.Agentic;

/// <summary>
/// Library-level endpoints: browse, folders, and orchestrators.
/// Routes: /api/v1/agentic/browse, /api/v1/agentic/folders, /api/v1/agentic/orchestrators
/// </summary>
internal static class AgenticLibraryEndpoints
{
    private const string Base = "/api/v1/agentic";

    public static IEndpointRouteBuilder MapAgenticLibraryApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapV1().MapGroup("/agentic").WithTags("Agentic Library");

        MapBrowse(group);
        MapFolders(group);
        MapOrchestrators(group);

        return endpoints;
    }

    // ── Browse ────────────────────────────────────────────────────────────────

    private static void MapBrowse(RouteGroupBuilder g)
    {
        g.MapGet(
                "/browse",
                async ([FromQuery] string? folderId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.BrowseAsync(folderId, ct).ConfigureAwait(false);
                    return Results.Ok(result);
                }
            )
            .WithName("BrowseAgenticLibrary")
            .WithSummary("Browse the agentic library tree");
    }

    // ── Folders ───────────────────────────────────────────────────────────────

    private static void MapFolders(RouteGroupBuilder g)
    {
        g.MapGet(
                "/folders",
                async ([FromQuery] string? parentFolderId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var folders = await repo.ListFoldersAsync(parentFolderId, ct).ConfigureAwait(false);
                    return Results.Ok(folders);
                }
            )
            .WithName("ListAgenticFolders")
            .WithSummary("List folders in the agentic library");

        g.MapPost(
                "/folders",
                async (CreateFolderRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.CreateFolderAsync(body.Name, body.ParentFolderId, ct).ConfigureAwait(false);
                    return result.Success ? Results.Created($"{Base}/folders/{result.Value!.FolderId}", result.Value) : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("CreateAgenticFolder")
            .WithSummary("Create a folder in the agentic library");

        g.MapPost(
                "/folders/{folderId}/rename",
                async (string folderId, RenameFolderRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.RenameFolderAsync(folderId, body.Name, ct).ConfigureAwait(false);
                    return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("RenameAgenticFolder")
            .WithSummary("Rename a folder");

        g.MapDelete(
                "/folders/{folderId}",
                async (string folderId, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.DeleteFolderAsync(folderId, ct).ConfigureAwait(false);
                    return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("DeleteAgenticFolder")
            .WithSummary("Delete a folder");

        g.MapPost(
                "/folders/{folderId}/move",
                async (string folderId, MoveFolderRequest body, [FromServices] IAgenticRepository repo, CancellationToken ct) =>
                {
                    var result = await repo.MoveFolderAsync(folderId, body.TargetParentFolderId, ct).ConfigureAwait(false);
                    return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: 422);
                }
            )
            .WithName("MoveAgenticFolder")
            .WithSummary("Move a folder to another parent");
    }

    // ── Orchestrators ─────────────────────────────────────────────────────────

    private static void MapOrchestrators(RouteGroupBuilder g)
    {
        g.MapGet(
                "/orchestrators",
                (HttpContext ctx) =>
                {
                    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Agentic.Orchestrators");
                    log.LogInformation("GET /orchestrators — resolving the team orchestrator catalog");
                    var catalog = ctx.RequestServices.GetService<ITeamOrchestratorCatalog>();
                    log.LogInformation("GET /orchestrators — catalog resolved: {IsNull}", catalog is null ? "NULL" : "OK");
                    if (catalog is null)
                        return Results.Problem("No team engine is available.", statusCode: 503);

                    var summaries = catalog.Describe().Select(d => new OrchestratorSummaryDto(d.Id, d.DisplayName, d.Description, d.WhenToUse)).ToList();
                    log.LogInformation("GET /orchestrators — returning {Count} orchestrators", summaries.Count);
                    return Results.Ok(summaries);
                }
            )
            .WithName("ListOrchestrators")
            .WithSummary("List all registered orchestrator IDs");

        g.MapGet(
                "/orchestrators/{orchestratorId}/connection-schema",
                (string orchestratorId, HttpContext ctx) =>
                {
                    var catalog = ctx.RequestServices.GetService<ITeamOrchestratorCatalog>();
                    if (catalog is null)
                        return Results.Problem("No team engine is available.", statusCode: 503);

                    var connectionRules = catalog.GetConnectionRules(orchestratorId);
                    if (connectionRules is null)
                        return Results.NotFound(new { error = $"Orchestrator '{orchestratorId}' not found." });

                    var rules = connectionRules.Select(r => new OrchestratorConnectionRuleDto(r.Type.ToString(), r.Cardinality.ToString(), r.Origin.ToString(), r.Description)).ToList();

                    return Results.Ok(new OrchestratorConnectionSchemaDto(orchestratorId, rules));
                }
            )
            .WithName("GetOrchestratorConnectionSchema")
            .WithSummary("Get the connection rules for a given orchestrator");
    }
}
