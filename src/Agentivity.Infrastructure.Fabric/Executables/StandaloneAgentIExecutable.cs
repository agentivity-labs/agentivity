using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// <see cref="IExecutable"/> adapter for the <see cref="DefaultExecutableResolver"/> agent path.
/// Builds standalone proxies at execute time (per-call) via <see cref="AgentProxyFactory.BuildStandalone"/>.
/// <para>
/// Unlike the proxy-injecting <see cref="AgentExecutable"/> constructor, this adapter defers
/// proxy construction to each <see cref="ExecuteAsync"/> call so it can use the <see cref="RunContext"/>
/// that is only known at execution time.
/// </para>
/// </summary>
public sealed class StandaloneAgentIExecutable : IExecutable
{
    private readonly AgentEntity _agentDefinition;
    private readonly AgentHost _agentHost;
    private readonly IInteractionRequestStore? _interactionStore;
    private readonly IRunEventBus? _eventBus;
    private readonly IAgentTelemetryEmitter? _telemetryEmitter;
    private readonly IRepository<RunContext, RunRecord>? _runRepository;

    public StandaloneAgentIExecutable(AgentEntity agentDefinition, AgentHost agentHost, IInteractionRequestStore? interactionStore, IRunEventBus? eventBus, IAgentTelemetryEmitter? telemetryEmitter, IRepository<RunContext, RunRecord>? runRepository = null)
    {
        _agentDefinition = agentDefinition ?? throw new ArgumentNullException(nameof(agentDefinition));
        _agentHost = agentHost ?? throw new ArgumentNullException(nameof(agentHost));
        _interactionStore = interactionStore;
        _eventBus = eventBus;
        _telemetryEmitter = telemetryEmitter;
        _runRepository = runRepository;
    }

    public IExecutableDefinition Definition => _agentDefinition;

    public async ValueTask<ExecutorResult> ExecuteAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default)
    {
        var (tools, session, hil, trace, humanLoopCtx) = AgentProxyFactory.BuildStandalone(_agentDefinition, _agentHost, _interactionStore, _eventBus, context);

        var resolver = AgentProxyFactory.BuildResolver(_agentDefinition, _agentHost, _eventBus, _telemetryEmitter, _runRepository);

        var executable = new AgentExecutable(_agentDefinition, _agentHost, _telemetryEmitter);

        var result = await executable.ExecuteAgentAsync(args, context, tools, session, hil, trace, humanLoopCtx, resolver, ct).ConfigureAwait(false);

        if (result.Status == ExecutableStatus.WaitingFor)
            return ExecutorResult.WaitingFor(result.WaitingForToken!);

        return ExecutorResult.FromText(result.Response) with
        {
            RoutingHint = result.OrchestrationHint,
        };
    }
}
