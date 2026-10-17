using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflow.Services.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static Agentivity.Infrastructure.Http.ApiEndpointHelpers;

namespace Agentivity.Host.Configs;

/// <summary>
/// Configuration file management endpoints.
/// Routes: /api/v1/configs
/// </summary>
public static class ConfigsEndpoints
{
    public static IEndpointRouteBuilder MapConfigsApi(this IEndpointRouteBuilder routes)
    {
        var v1 = routes.MapV1();

        v1.MapGet("/configs", (ConfigurationService configService) => Results.Ok(configService.ListFiles()))
            .WithSummary("List the configuration files");

        v1.MapGet(
            "/configs/{*path}",
            async (string path, ConfigurationService configService, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(path))
                    return ApiValidationError(code: "configuration_path_required", userMessage: "A configuration path is required.", detail: "Configuration path is required.");

                // A configuration is asked for by its name, never by a place on the disk (refused as configuration_path_invalid).
                ConfigurationRepository.NameOf(path);
                if (!configService.Exists(path))
                    return ApiNotFound(code: "configuration_not_found", userMessage: "This configuration file could not be found.", detail: "Configuration file not found.", context: new { path });

                var content = await configService.LoadAsync<object>(path, ct);
                return content is not null ? Results.Ok(content) : ApiNotFound(code: "configuration_not_found", userMessage: "This configuration file could not be loaded.", detail: "Failed to load configuration.", context: new { path });
            }
        )
            .WithSummary("Read a configuration file");

        v1.MapPut(
            "/configs/{*path}",
            async (string path, System.Text.Json.JsonElement content, ConfigurationService configService, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(path))
                    return ApiValidationError(code: "configuration_path_required", userMessage: "A configuration path is required.", detail: "Configuration path is required.");

                var json = System.Text.Json.JsonSerializer.Serialize(content, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await configService.SaveRawAsync(path, json, ct);
                return Results.Ok(new { status = "saved", path });
            }
        )
            .WithSummary("Save a configuration file");

        return routes;
    }
}
