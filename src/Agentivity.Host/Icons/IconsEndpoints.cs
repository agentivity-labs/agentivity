using Agentivity.Infrastructure.Identity;
using Agentivity.Domain.Icons;
using Agentivity.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Host.Icons;

/// <summary>
/// The catalog of icons a user can choose from: <c>GET /api/v1/icons</c>. Generic — organised by icon set
/// (<c>type</c>, today only <c>material</c>) so any screen that lets a user pick an icon (team members, and later anything
/// else) reads the same list.
/// </summary>
internal static class IconsEndpoints
{
    public static IEndpointRouteBuilder MapIconsApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapV1()
            .MapGet(
                "/icons",
                ([FromQuery] string? type, [FromQuery] string? q) =>
                {
                    if (!string.IsNullOrWhiteSpace(type) && !IconCatalog.Types.Contains(type))
                        return Results.BadRequest(new { error = $"Unknown icon type '{type}'. Available: {string.Join(", ", IconCatalog.Types)}." });

                    return Results.Ok(IconCatalog.Find(string.IsNullOrWhiteSpace(type) ? null : type, q));
                }
            )
            .WithName("ListIcons")
            .WithTags("Icons")
            .WithSummary("List the icons a user can choose from, optionally filtered by set (type) or by name (q)")
            .Publishable(Agentivity.Infrastructure.Identity.PublishableAccess.Anyone);

        return endpoints;
    }
}
