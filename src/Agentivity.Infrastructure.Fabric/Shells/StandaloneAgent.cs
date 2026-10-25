using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Interactions;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Standalone shell for executing an <see cref="AgentEntity"/> directly —
/// outside a workflow, team, or any other orchestrated context.
/// <para>
/// This is the third symmetric shell alongside <c>AiNode</c> (workflow) and
/// <see cref="TeamAgent"/> (team member). It uses <see cref="AgentProxyFactory"/> to build
/// context-specific proxies and delegates to <see cref="AgentExecutable.ExecuteAgentAsync"/> —
/// the canonical entry point to <see cref="AgentHost.RunAgentAsync"/>.
/// </para>
/// <para>
/// Use <see cref="RunAsync(AgentEntity,IReadOnlyDictionary{string,object?},RunContext,CancellationToken)"/>
/// for direct programmatic invocations (API endpoints, SDK calls, script triggers).
/// When the agent is accessed through the catalogue registry, use <see cref="StandaloneAgentIExecutable"/>.
/// </para>
/// </summary>
public sealed class StandaloneAgent(AgentHost agentHost, IInteractionRequestStore? interactionStore = null, IRunEventBus? eventBus = null, IAgentTelemetryEmitter? telemetryEmitter = null, Agentivity.Infrastructure.Repository.IRepository<RunContext, RunRecord>? runRepository = null)
{
    /// <summary>
    /// Executes the given agent and returns the final run result.
    /// </summary>
    /// <param name="agent">The agent entity to execute.</param>
    /// <param name="args">
    /// Input arguments — must contain at least <c>input</c> (the user goal / prompt).
    /// </param>
    /// <param name="context">
    /// Execution context that carries RunId, HIL flag, execution chain, and OTel correlation.
    /// Start a root context with <see cref="RunContext.StartNew"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask<AgentRunResult> RunAsync(AgentEntity agent, IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);

        var (tools, session, hil, trace, preparedHumanLoopContext) = AgentProxyFactory.BuildStandalone(agent, agentHost, interactionStore, eventBus, context);
        var resolver = AgentProxyFactory.BuildResolver(agent, agentHost, eventBus, telemetryEmitter, runRepository);
        var executable = new AgentExecutable(agent, agentHost, telemetryEmitter);
        return executable.ExecuteAgentAsync(args, context, tools, session, hil, trace, preparedHumanLoopContext, resolver, ct);
    }

    /// <summary>
    /// Convenience overload — creates a fresh root <see cref="RunContext"/> automatically.
    /// </summary>
    public ValueTask<AgentRunResult> RunAsync(AgentEntity agent, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        var context = RunContext.StartNew(RunKind.Agent, agent.Id);
        return RunAsync(agent, args, context, ct);
    }
}
