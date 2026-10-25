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
        // An agent called by something else (a step of a workflow) is a run of its own in the tree of runs, under the run that
        // called it: the question it asks belongs to that run, and a person's answer finds its way through it. An agent does
        // not record its run itself, so it is done here, as a team does for its members. The run of an agent started on its
        // own has no parent: whoever started it records it, and reports how it ends.
        var called = context.ParentRunId is not null ? _runRepository : null;
        await ChildRunLedger.RegisterAsync(called, context, ct).ConfigureAwait(false);

        ExecutorResult outcome;
        try
        {
            outcome = await RunAsync(args, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ChildRunLedger.SetStateAsync(called, context.RunId, Agentivity.Domain.Run.RunStatus.Cancelled, ct: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await ChildRunLedger.SetStateAsync(called, context.RunId, Agentivity.Domain.Run.RunStatus.Failed, error: ex.Message, ct: CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ChildRunLedger.SetOutcomeAsync(called, context.RunId, outcome, ct).ConfigureAwait(false);
        return outcome;
    }

    private async ValueTask<ExecutorResult> RunAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct)
    {
        var (tools, session, hil, trace, humanLoopCtx) = AgentProxyFactory.BuildStandalone(_agentDefinition, _agentHost, _interactionStore, _eventBus, context);

        var resolver = AgentProxyFactory.BuildResolver(_agentDefinition, _agentHost, _eventBus, _telemetryEmitter, _runRepository);

        var executable = new AgentExecutable(_agentDefinition, _agentHost, _telemetryEmitter);

        var result = await executable.ExecuteAgentAsync(args, context, tools, session, hil, trace, humanLoopCtx, resolver, ct).ConfigureAwait(false);
        return AgentExecutable.ResultOf(result, _agentDefinition);
    }
}
