using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.DataTables.DependencyInjection;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// DI registration for the Fabric layer — the crossing point for all entities, not just
/// executables: resolves <see cref="IExecutable"/> for agent/team/workflow and wires the
/// composable executor, AND aggregates non-executable resource entities (DataTables today;
/// future Env/Memories/Secret/Scheduling resources join here too). Callers (AgentivityHost) only
/// ever need to call <see cref="AddFabric"/> — they don't wire individual entity kinds themselves.
/// </summary>
public static class FabricServiceCollectionExtensions
{
    public static IServiceCollection AddFabric(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<StandaloneAgent>(sp => new StandaloneAgent(sp.GetRequiredService<AgentHost>(), sp.GetService<IInteractionRequestStore>(), sp.GetService<IRunEventBus>(), sp.GetService<IAgentTelemetryEmitter>()));

        services.AddSingleton<Agentivity.Infrastructure.Interactions.IResumableEngine, StandaloneAgentResumableEngine>();

        // Lazy<ITeamEngine> breaks the circular dependency:
        //   IExecutableResolver → ITeamEngine → OrchestratorRegistry → IExecutableResolver
        services.TryAddSingleton<IExecutableResolver>(sp => new DefaultExecutableResolver(
            sp.GetRequiredService<IAgenticRepository>(),
            sp.GetRequiredService<IEntityRepository<WorkflowEntity>>(),
            sp.GetRequiredService<AgentHost>(),
            new Lazy<ITeamEngine>(() => sp.GetRequiredService<ITeamEngine>()),
            sp.GetRequiredService<WorkflowEngine>(),
            sp.GetService<IInteractionRequestStore>(),
            sp.GetService<IRunEventBus>(),
            sp.GetService<IAgentTelemetryEmitter>(),
            sp.GetService<IRepository<RunContext, RunRecord>>()
        ));

        // Non-executable resource entities — DataTables is the first; future resource kinds
        // (Env, Memories, Secret, Scheduling) get their own AddXxx() in their own project and are
        // aggregated here alongside it, same as executables above.
        services.AddDataTables(configuration);

        return services;
    }
}
