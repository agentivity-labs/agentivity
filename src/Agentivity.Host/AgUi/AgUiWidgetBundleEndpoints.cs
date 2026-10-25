using Agentivity.Infrastructure.Identity;
using System.Text.Json;
using Agentivity.Infrastructure.AgUi;

namespace Agentivity.Host.AgUi;

/// <summary>
/// AG-UI widget bundle discovery endpoints.
///
/// GET /api/v1/ag-ui/bundles              — list all registered bundles (summary)
/// GET /api/v1/ag-ui/bundles/{bundleId}   — bundle detail with full widget descriptors
/// </summary>
public static class AgUiWidgetBundleEndpoints
{
    public static IEndpointRouteBuilder MapAgUiWidgetBundleApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/ag-ui/bundles").WithTags("AG-UI");

        group
            .MapGet("/", (IAgUiWidgetBundleRegistry registry) => Results.Ok(registry.GetAll().Select(b => new AgUiBundleSummaryResponse(b.Id, b.DisplayName, b.Version, b.Description, b.Widgets.Count))))
            .WithName("GetAgUiBundles")
            .WithSummary("Returns all registered AG-UI widget bundles")
            .Publishable(Agentivity.Infrastructure.Identity.PublishableAccess.Anyone)
            .Produces<IEnumerable<AgUiBundleSummaryResponse>>();

        group
            .MapGet(
                "/{bundleId}",
                (string bundleId, IAgUiWidgetBundleRegistry registry) =>
                {
                    var bundle = registry.GetById(bundleId);
                    if (bundle is null)
                        return Results.NotFound(new { error = $"Bundle '{bundleId}' is not registered." });

                    return Results.Ok(new AgUiBundleDetailResponse(bundle.Id, bundle.DisplayName, bundle.Version, bundle.Description, bundle.Widgets.Select(w => new AgUiWidgetDescriptorResponse(w.TypeKey, w.Category, w.Description, ParseSchema(w.ParametersSchema), w.RequiresHumanResponse)).ToList()));
                }
            )
            .WithName("GetAgUiBundleWidgets")
            .WithSummary("Returns the full widget descriptor list for a specific bundle")
            .Publishable(Agentivity.Infrastructure.Identity.PublishableAccess.Anyone)
            .Produces<AgUiBundleDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>The widget's parameter schema as a JSON object, so a client reads it directly instead of unescaping a string; null when it is missing or not valid JSON.</summary>
    private static JsonElement? ParseSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return null;
        try
        {
            using var document = JsonDocument.Parse(schema);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record AgUiBundleSummaryResponse(string Id, string DisplayName, string Version, string Description, int WidgetCount);

public sealed record AgUiBundleDetailResponse(string Id, string DisplayName, string Version, string Description, IReadOnlyList<AgUiWidgetDescriptorResponse> Widgets);

/// <param name="ParametersSchema">The JSON schema of the widget's data, as an object.</param>
/// <param name="RequiresHumanResponse">True when the widget waits for the person to answer before the run goes on.</param>
public sealed record AgUiWidgetDescriptorResponse(string TypeKey, string Category, string Description, JsonElement? ParametersSchema = null, bool RequiresHumanResponse = false);
