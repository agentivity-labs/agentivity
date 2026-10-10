using Agentivity.Infrastructure.Interactions.Forms.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Interactions.Forms.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFormsInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<FormsService>();
        return services;
    }
}
