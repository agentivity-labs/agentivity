using Agentivity.Infrastructure.Core.Llm.Moderation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Agentic.Agent.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AgentHost"/> using the <see cref="IAgentEngine"/> provided by
    /// the dynamically loaded engine module (e.g. AgentEngineV1).
    /// Must be called after the module loader has registered <see cref="IAgentEngine"/>.
    /// </summary>
    public static IServiceCollection AddAgentHost(this IServiceCollection services)
    {
        services.AddSingleton<AgentHost>(sp =>
        {
            var engine = sp.GetRequiredService<IAgentEngine>();
            var moderationService = sp.GetService<IModerationService>();
            var turnConclusionFactory = sp.GetService<ITurnConclusionToolFactory>();
            return new AgentHost(engine, moderationService, turnConclusionFactory);
        });

        return services;
    }
}
