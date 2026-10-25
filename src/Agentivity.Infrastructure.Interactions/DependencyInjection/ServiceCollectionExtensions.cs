using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Interactions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the generic interaction infrastructure shared by all channels (Chat, Forms, Email, Slack…):
    /// <see cref="IInteractionBridge"/>, <see cref="IInteractionThreadStore"/>.
    /// Call once at startup before registering channel-specific services.
    /// </summary>
    public static IServiceCollection AddInteractionsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InteractionThreadStorageOptions>(configuration.GetSection(InteractionThreadStorageOptions.SectionName));
        // Single resume path for every run kind — engines contribute IResumableEngine implementations.
        services.TryAddSingleton<DurableRunService>();
        services.TryAddSingleton<IInteractionBridge, InteractionBridge>();
        services.TryAddSingleton<IInteractionThreadStore, FileInteractionThreadStore>();
        return services;
    }
}
