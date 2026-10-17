using Agentivity.Infrastructure.Features;
using Agentivity.Infrastructure.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.Modules;

public static class ModuleEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapModuleCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapV1().MapGet("/features", (IFeatureDiscoveryService discovery) => Results.Ok(discovery.GetDiscovery()))
            .WithSummary("List the features available on this server");
        return endpoints;
    }
}
