using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.Plugins;

/// <summary>
/// Contributes HTTP endpoints to the host application.
/// Plugins that need non-HTTP transports should register their own hosted services in <see cref="IWorkflowPlugin.ConfigureServices"/>.
/// </summary>
public interface IEndpointContributor
{
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
