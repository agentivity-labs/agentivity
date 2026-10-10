using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Repository.Entities;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Default implementation of <see cref="IExecutableResolver"/>.
/// Dynamically resolves the correct <see cref="IExecutable"/> for a given entity id
/// by searching across agent, team, and workflow catalogs.
/// Resolution order: agent → team → workflow.
/// When <paramref name="kind"/> is provided the search is restricted to that kind.
/// </summary>
public sealed class DefaultExecutableResolver(
    IAgenticRepository agenticLibrary,
    IEntityRepository<WorkflowEntity> workflowRepository,
    AgentHost agentHost,
    Lazy<ITeamEngine> agenticEngine,
    WorkflowEngine workflowEngine,
    IInteractionRequestStore? interactionStore = null,
    IRunEventBus? eventBus = null,
    IAgentTelemetryEmitter? telemetryEmitter = null,
    IRepository<RunContext, RunRecord>? runRepository = null
) : IExecutableResolver
{
    public async ValueTask<IExecutable> ResolveAsync(string entityId, CancellationToken ct = default)
    {
        // Try agent first.
        var agentResult = await agenticLibrary.GetAgentAsync(entityId, ct).ConfigureAwait(false);
        if (agentResult.IsSuccess && agentResult.Value is not null)
            return new StandaloneAgentIExecutable(agentResult.Value, agentHost, interactionStore, eventBus, telemetryEmitter, runRepository);

        // Try team.
        var teamResult = await agenticLibrary.GetTeamAsync(entityId, ct).ConfigureAwait(false);
        if (teamResult.IsSuccess && teamResult.Value is not null)
            return new TeamExecutable(teamResult.Value, agenticEngine.Value);

        // Try workflow.
        var workflowResult = await workflowRepository.GetAsync(entityId, ct).ConfigureAwait(false);
        if (workflowResult.IsSuccess && workflowResult.Value is not null)
            return new WorkflowExecutable(workflowResult.Value, workflowEngine);

        throw new InvalidOperationException($"Entity '{entityId}' could not be resolved as an agent, team, or workflow.");
    }

    public IExecutable Resolve(ExecutableKind kind, string entityId) =>
        kind switch
        {
            ExecutableKind.Workflow => new WorkflowExecutable(entityId, entityId, workflowEngine),
            _ => throw new InvalidOperationException($"Synchronous Resolve is not supported for kind '{kind}'. Use ResolveAsync instead."),
        };

    public async ValueTask<IReadOnlyList<IExecutableDefinition>> ListAsync(ExecutableKind? filter = null, CancellationToken ct = default)
    {
        var result = new List<IExecutableDefinition>();

        if (filter is null or ExecutableKind.Agent)
        {
            var agents = await agenticLibrary.ListAgentsAsync(cancellationToken: ct).ConfigureAwait(false);
            result.AddRange(agents);
        }

        if (filter is null or ExecutableKind.Team)
        {
            var teams = await agenticLibrary.ListTeamsAsync(cancellationToken: ct).ConfigureAwait(false);
            result.AddRange(teams);
        }

        if (filter is null or ExecutableKind.Workflow)
        {
            var workflows = await workflowRepository.ListAsync(cancellationToken: ct).ConfigureAwait(false);
            result.AddRange(workflows);
        }

        return result;
    }
}
