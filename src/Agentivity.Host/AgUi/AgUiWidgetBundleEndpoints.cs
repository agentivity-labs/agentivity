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
            .Produces<IEnumerable<AgUiBundleSummaryResponse>>();

        group
            .MapGet(
                "/{bundleId}",
                (string bundleId, IAgUiWidgetBundleRegistry registry) =>
                {
                    var bundle = registry.GetById(bundleId);
                    if (bundle is null)
                        return Results.NotFound(new { error = $"Bundle '{bundleId}' is not registered." });

                    return Results.Ok(new AgUiBundleDetailResponse(bundle.Id, bundle.DisplayName, bundle.Version, bundle.Description, bundle.Widgets.Select(w => new AgUiWidgetDescriptorResponse(w.TypeKey, w.Category, w.Description)).ToList()));
                }
            )
            .WithName("GetAgUiBundleWidgets")
            .WithSummary("Returns the full widget descriptor list for a specific bundle")
            .Produces<AgUiBundleDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        return routes;
    }
}

public sealed record AgUiBundleSummaryResponse(string Id, string DisplayName, string Version, string Description, int WidgetCount);

public sealed record AgUiBundleDetailResponse(string Id, string DisplayName, string Version, string Description, IReadOnlyList<AgUiWidgetDescriptorResponse> Widgets);

public sealed record AgUiWidgetDescriptorResponse(string TypeKey, string Category, string Description);
