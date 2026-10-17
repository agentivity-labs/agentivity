using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Models;
using Agentivity.Infrastructure.Workflow.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Host.Settings;

/// <summary>
/// Application settings endpoints.
/// Routes: /api/v1/settings
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapGet("/settings", async (AppSettingsService settingsService, CancellationToken ct) => Results.Ok(await settingsService.GetAsync(ct)))
            .WithSummary("Read the application settings");

        v1.MapPut(
            "/settings",
            async (AppSettings settings, AppSettingsService settingsService, CancellationToken ct) =>
            {
                await settingsService.SaveAsync(settings, ct);
                return Results.Ok(new { status = "saved" });
            }
        )
            .WithSummary("Save the application settings");

        return routes;
    }
}
