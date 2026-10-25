using Agentivity.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Workflow.Catalog.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default built-in catalog plugin.
    /// The workflow engine itself is registered by <c>AddInfrastructureWorkflow</c>.
    /// </summary>
    public static IServiceCollection AddOrchestrationCore(this IServiceCollection services)
    {
        _ = services.AddBuiltInWorkflowPlugin<CatalogPlugin>();
        return services;
    }
}
