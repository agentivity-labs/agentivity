using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Host.Entities;

/// <summary>
/// Debug/inspection endpoint for all run types (workflow, agent, team).
/// Reads directly from the unified <see cref="IRunRepository"/> — no per-type adapters.
/// Route: GET /api/v1/runs/{runId}/inspector
/// </summary>
internal static class RunInspectorEndpoints
{
    public static IEndpointRouteBuilder MapRunInspectorApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapV1()
            .MapGet(
                "/runs/{runId}/inspector",
                async (string runId, [FromServices] IRepository<RunContext, RunRecord> runRepository) =>
                {
                    var ctx = await runRepository.FindAsync(runId).ConfigureAwait(false);
                    var snapshot = ctx?.ToInspectorSnapshot();
                    if (snapshot is null)
                        return Results.NotFound(new { error = $"No inspector data found for run '{runId}'." });
                    return Results.Ok(snapshot);
                }
            )
            .WithName("GetRunInspector")
            .WithSummary("Debug/inspection snapshot for a run (workflow, agent, team)")
            .WithTags("Runs");

        return endpoints;
    }
}
