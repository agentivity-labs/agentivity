using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.Modules;

public static class ModuleEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapModuleCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapV1().MapGet("/features", (IFeatureDiscoveryService discovery) => Results.Ok(discovery.GetDiscovery()))
            .WithSummary("List the features available on this server")
            // The Studio reads it before anyone has signed in: it says whether to ask for it.
            .PublicRoute("What the instance offers, read by the Studio before sign-in to know whether to ask for it.");
        return endpoints;
    }
}
