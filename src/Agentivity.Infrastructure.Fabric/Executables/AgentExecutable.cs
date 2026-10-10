using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Infrastructure.Agentic;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Interactions;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Observability.Contracts.Events.Agent;
using Agentivity.Infrastructure.Workflows.Interactions;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Canonical entry point between any execution shell and <see cref="AgentHost.RunAgentAsync"/>.
/// <para>
/// Two construction paths:
/// <list type="bullet">
///   <item>Proxy-injecting constructor — used by <see cref="StandaloneAgent"/> and <see cref="TeamAgent"/>.
///   Proxies are captured at construction; <see cref="ExecuteAsync(IReadOnlyDictionary{string,object?},RunContext,CancellationToken)"/>
///   is pure (IExecutable path).</item>
///   <item>Lightweight constructor — used by <c>AiNode</c> and the registry path.
///   Proxies are passed to <see cref="ExecuteAgentAsync"/> at call time.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AgentExecutable : IExecutable
{
    private readonly AgentEntity? _agentDefinition;
    private readonly AgentHost _agentHost;
    private readonly IAgentToolsProxy? _tools;
    private readonly IAgentSessionProxy? _session;
    private readonly IAgentHilProxy? _hil;
    private readonly IAgentTraceProxy? _trace;
    private readonly PreparedAgentHumanLoopContext? _humanLoopCtx;
    private readonly IAgentDefinitionResolver? _resolver;
    private readonly IAgentTelemetryEmitter? _telemetryEmitter;
    private readonly bool _suppressDirectMessageEvent;

    /// <summary>
    /// Lightweight constructor used by <c>AiNode</c> and callers that pass proxies directly
    /// to <see cref="ExecuteAgentAsync"/>.
    /// </summary>
    public AgentExecutable(AgentEntity? agentDefinition, AgentHost agentHost, IAgentTelemetryEmitter? telemetryEmitter = null)
    {
        _agentDefinition = agentDefinition;
        _agentHost = agentHost ?? throw new ArgumentNullException(nameof(agentHost));
        _telemetryEmitter = telemetryEmitter;
    }

    /// <summary>
    /// Proxy-injecting constructor used by <see cref="StandaloneAgent"/> and <see cref="TeamAgent"/>.
    /// <see cref="ExecuteAsync(IReadOnlyDictionary{string,object?},RunContext,CancellationToken)"/> uses
    /// the stored proxies — no proxy building at execute time.
    /// </summary>
    public AgentExecutable(
        AgentEntity? agentDefinition,
        AgentHost agentHost,
        IAgentToolsProxy tools,
        IAgentSessionProxy session,
        IAgentHilProxy hil,
        IAgentTraceProxy trace,
        PreparedAgentHumanLoopContext humanLoopCtx,
        IAgentDefinitionResolver resolver,
        IAgentTelemetryEmitter? telemetryEmitter = null,
        bool suppressDirectMessageEvent = false
    )
    {
        _agentDefinition = agentDefinition;
        _agentHost = agentHost ?? throw new ArgumentNullException(nameof(agentHost));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _hil = hil ?? throw new ArgumentNullException(nameof(hil));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _humanLoopCtx = humanLoopCtx ?? throw new ArgumentNullException(nameof(humanLoopCtx));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _telemetryEmitter = telemetryEmitter;
        _suppressDirectMessageEvent = suppressDirectMessageEvent;
    }

    public IExecutableDefinition Definition => _agentDefinition ?? throw new InvalidOperationException("This AgentExecutable was constructed without an AgentEntity (workflow shell usage) and has no IExecutableDefinition.");

    /// <summary>
    /// IExecutable path — uses proxies injected at construction time.
    /// Requires the proxy-injecting constructor.
    /// </summary>
    public async ValueTask<ExecutorResult> ExecuteAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default)
    {
        if (_tools is null || _session is null || _hil is null || _trace is null || _humanLoopCtx is null || _resolver is null)
            throw new InvalidOperationException("ExecuteAsync(args, context) requires an AgentExecutable constructed with the proxy-injecting constructor.");

        var agentResult = await RunCoreAsync(args, context, _tools, _session, _hil, _trace, _humanLoopCtx, _resolver, _suppressDirectMessageEvent, ct).ConfigureAwait(false);

        if (agentResult.Status == ExecutableStatus.WaitingFor)
            return ExecutorResult.WaitingFor(agentResult.WaitingForToken!);

        return ExecutorResult.FromText(agentResult.Response) with
        {
            RoutingHint = agentResult.OrchestrationHint,
        };
    }

    /// <summary>
    /// Called by <c>AiNode</c>, <see cref="StandaloneAgent"/>, and <see cref="TeamAgent"/>.
    /// Proxies are supplied by the caller — stored proxies (if any) are not used.
    /// Returns the full <see cref="AgentRunResult"/> for callers that need more than the final text.
    /// </summary>
    public ValueTask<AgentRunResult> ExecuteAgentAsync(
        IReadOnlyDictionary<string, object?> args,
        RunContext context,
        IAgentToolsProxy toolsProxy,
        IAgentSessionProxy sessionProxy,
        IAgentHilProxy hilProxy,
        IAgentTraceProxy traceProxy,
        PreparedAgentHumanLoopContext preparedHumanLoopContext,
        IAgentDefinitionResolver resolver,
        CancellationToken ct = default,
        bool suppressDirectMessageEvent = false
    )
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(toolsProxy);
        ArgumentNullException.ThrowIfNull(sessionProxy);
        ArgumentNullException.ThrowIfNull(hilProxy);
        ArgumentNullException.ThrowIfNull(traceProxy);
        ArgumentNullException.ThrowIfNull(preparedHumanLoopContext);
        ArgumentNullException.ThrowIfNull(resolver);

        return RunCoreAsync(args, context, toolsProxy, sessionProxy, hilProxy, traceProxy, preparedHumanLoopContext, resolver, suppressDirectMessageEvent, ct);
    }

    // ── Internal pipeline ─────────────────────────────────────────────────────

    private async ValueTask<AgentRunResult> RunCoreAsync(
        IReadOnlyDictionary<string, object?> args,
        RunContext context,
        IAgentToolsProxy toolsProxy,
        IAgentSessionProxy sessionProxy,
        IAgentHilProxy hilProxy,
        IAgentTraceProxy traceProxy,
        PreparedAgentHumanLoopContext preparedHumanLoopContext,
        IAgentDefinitionResolver resolver,
        bool suppressDirectMessageEvent,
        CancellationToken ct
    )
    {
        var resolved = await resolver.ResolveAsync(args, context, ct).ConfigureAwait(false);

        // ── Session ───────────────────────────────────────────────────────────
        var sessionContext = await sessionProxy.LoadAsync(ct).ConfigureAwait(false);

        var agentStartedAtUtc = DateTime.UtcNow;

        // ── Lifecycle telemetry: started/failed ───────────────────────────────
        if (_telemetryEmitter is not null)
        {
            await _telemetryEmitter.EmitAsync(context.ToCorrelationContext(), new AgentStartedEvent(context.EntityId, resolved.ModeRuntime.ModeId, agentStartedAtUtc), ct).ConfigureAwait(false);
        }

        AgentRunResult agentResult;
        try
        {
            agentResult = await _agentHost
                .RunAgentAsync(
                    resolved.SystemPrompt,
                    resolved.ResolvedPrompt,
                    string.Empty,
                    resolved.MemoryContext,
                    resolved.ModeRuntime,
                    resolved.RuntimeArguments,
                    resolved.RuntimeParameters,
                    resolved.ResolvedPolicies,
                    resolved.ResolvedGuardrails,
                    resolved.PersistSession,
                    sessionContext,
                    toolsProxy,
                    sessionProxy,
                    hilProxy,
                    traceProxy,
                    resolved.RuntimePort,
                    resolved.ContextServices,
                    context,
                    ct,
                    resolved.Images
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_telemetryEmitter is not null)
            {
                await _telemetryEmitter.EmitAsync(context.ToCorrelationContext(), new AgentFailedEvent(context.EntityId, resolved.ModeRuntime.ModeId, agentStartedAtUtc, DateTime.UtcNow, ex.Message), ct).ConfigureAwait(false);
            }

            throw;
        }

        agentResult = agentResult with { MemoryContextUsed = resolved.MemoryContext };

        // ── Clear the per-run human-loop clarification ledger ─────────────────
        HumanLoopConversationContextHelper.ClearClarificationLedger(Guid.Parse(context.RunId));

        // ── Status + completion telemetry ─────────────────────────────────────
        resolved.ContextServices.PublishStatus?.Invoke($"AI agent completed after {agentResult.ExecutedIterations} iteration(s).");

        if (_telemetryEmitter is not null)
        {
            await _telemetryEmitter.EmitAsync(context.ToCorrelationContext(), new AgentCompletedEvent(context.EntityId, resolved.ModeRuntime.ModeId, agentStartedAtUtc, DateTime.UtcNow, agentResult.ExecutedIterations, agentResult.TotalTokensUsed), ct).ConfigureAwait(false);
        }

        // ── Publish the assistant's final response on the chat channel ─────────
        // Publishing requires an explicit, bound ChatChannelNode (hilProxy.IsEnabled) — a
        // deliberate user choice, like any other interaction channel (email, Slack, forms).
        // There is intentionally no bus-only fallback: an agent with no chat channel produces
        // no chat message, the same way it produces no email without an EmailChannelNode.
        // Skip publishing when the finish reason is max_iterations — the fallback error string
        // is an internal signal, not a real agent response intended for the conversation.
        var isIterationLimitFallback = string.Equals(agentResult.FinishReason, "max_iterations_reached", StringComparison.Ordinal);
        // Skip publishing a handoff turn's Response here — it's the team_handoff "message" argument
        // (AgentManifest.cs:421). TeamAgent.ExecuteAsync delivers it separately, right after it gets
        // this result back and knows the routing decision — delivering it here too would double it.
        var isHandoffTurn = agentResult.OrchestrationHint is not null;
        if (hilProxy.IsEnabled && !string.IsNullOrWhiteSpace(agentResult.Response) && !suppressDirectMessageEvent && !isIterationLimitFallback && !isHandoffTurn)
        {
            // Same mechanism used for interact_with_human deliveries and HIL suspend/resume
            // (ChatChannel + IAgentHilProxy.SendWithoutWaitingAsync) — persists (thread store,
            // survives reconnects) and publishes in one call; ChatChannel.ResolveThreadAsync
            // already owns thread identity.
            var humanLoopToolDefinition = hilProxy.ToolDefinitions.FirstOrDefault(tool => AgentHumanLoopToolDefinitionJson.GetResultMode(tool) == AgentHumanLoopResultMode.FreeText);
            var pendingReview = HumanLoopHelper.BuildPlainTextPendingReview(humanLoopToolDefinition, agentResult.Response, agentResult.ExecutedIterations);
            await hilProxy.SendWithoutWaitingAsync(pendingReview, ct).ConfigureAwait(false);
        }

        return agentResult;
    }
}
