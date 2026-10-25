using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Extension methods for registering graph engine services into an
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class GraphHostServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provided <paramref name="graphHost"/> instance as the singleton <see cref="IGraphHost"/>.
    /// Use this when an explicit host instance is already constructed (e.g. by a module).
    /// </summary>
    public static IServiceCollection AddGraphHost(this IServiceCollection services, IGraphHost graphHost)
    {
        ArgumentNullException.ThrowIfNull(graphHost);
        services.TryAddSingleton(graphHost);
        return services;
    }
}
