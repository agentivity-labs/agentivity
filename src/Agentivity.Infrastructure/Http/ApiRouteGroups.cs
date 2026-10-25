using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.Http;

public static class ApiRouteGroups
{
    public static RouteGroupBuilder MapV1(this IEndpointRouteBuilder endpoints) => endpoints.MapGroup("/api/v1");

    // public static RouteGroupBuilder MapV2(this IEndpointRouteBuilder endpoints) =>
    //     endpoints.MapGroup("/api/v2");
}
