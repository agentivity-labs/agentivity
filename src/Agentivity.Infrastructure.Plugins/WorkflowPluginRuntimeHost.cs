using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.Plugins;

public sealed class WorkflowPluginRuntimeHost(IEnumerable<IEndpointContributor> endpointContributors)
{
    private readonly IReadOnlyList<IEndpointContributor> _endpointContributors = endpointContributors.ToList();

    public void MapCapabilities(IEndpointRouteBuilder endpoints)
    {
        foreach (var endpointContributor in _endpointContributors)
        {
            endpointContributor.MapEndpoints(endpoints);
        }
    }
}
