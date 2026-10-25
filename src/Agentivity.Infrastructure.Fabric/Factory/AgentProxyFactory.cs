using System.Text.Json;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Overrides applied when an agent runs as a MEMBER of a larger execution (a team, for instance) rather than on its own:
/// a different effective "may ask the human" setting, a decoration of the tools proxy, and the parent run the human-loop
/// interaction must be associated with.
/// </summary>
/// <param name="AllowHumanQuestions">Replaces the agent's own <c>allows_human_questions</c> when set.</param>
/// <param name="DecorateTools">Wraps the standalone tools proxy; receives the effective "may ask the human" setting.</param>
/// <param name="RunRepository">Run repository handed to the human-loop proxy (to find the parent run on resume).</param>
/// <param name="ParentRunId">Run the human-loop interaction belongs to (the parent execution, not the agent's own sub-run).</param>
/// <param name="DisplayName">How the member is named in human-loop messages.</param>
public sealed record AgentMemberOverrides(
    bool? AllowHumanQuestions = null,
    Func<IAgentToolsProxy, bool, IAgentToolsProxy>? DecorateTools = null,
    Agentivity.Infrastructure.Repository.IRepository<RunContext, RunRecord>? RunRepository = null,
    string? ParentRunId = null,
    string? DisplayName = null
);

/// <summary>
/// Builds the proxy set (<see cref="IAgentToolsProxy"/>, <see cref="IAgentSessionProxy"/>,
/// <see cref="IAgentHilProxy"/>, <see cref="IAgentTraceProxy"/>) for a given
/// <see cref="AgentEntity"/> and execution context. An execution that hosts agents as members passes
/// <see cref="AgentMemberOverrides"/>; the factory itself knows nothing about teams.
/// </summary>
public static class AgentProxyFactory
{
    private const string AiNodeType = "ai.node";

    /// <summary>
    /// Builds the proxy set for an agent run — standalone by default, or as a member of a larger execution
    /// when <paramref name="member"/> is supplied. Used by <see cref="StandaloneAgent"/>, by
    /// <see cref="StandaloneAgentIExecutable"/> (registry path) and by member shells such as a team's.
    /// </summary>
    public static (IAgentToolsProxy tools, IAgentSessionProxy session, IAgentHilProxy hil, IAgentTraceProxy trace, PreparedAgentHumanLoopContext humanLoopContext) BuildStandalone(
        AgentEntity agentDefinition,
        AgentHost agentHost,
        IInteractionRequestStore? interactionStore,
        IRunEventBus? eventBus,
        RunContext context,
        ILogger? logger = null,
        AgentMemberOverrides? member = null
    )
    {
        var (aiNodeId, aiNodeParams) = ResolveAiNode(agentDefinition);

        var allowsHumanQuestions = ReadBool(aiNodeParams, "allows_human_questions") ?? true;
        // A member may have the human channel closed regardless of the agent's own setting. Applied here — not just
        // to the turn-conclusion tool the decorator builds: 'interact_with_human' (gated by allowsHumanQuestions via
        // HilWaitProxy below) is a SEPARATE tool a member could otherwise call directly, bypassing the
        // turn-conclusion tool's own needs_input action entirely.
        var effectiveAllowsHumanQuestions = member?.AllowHumanQuestions ?? allowsHumanQuestions;

        var toolPort = aiNodeId is not null ? StandaloneAgentToolProxy.TryCreate(agentDefinition, aiNodeId, context.ToolState) : null;
        IAgentToolsProxy toolsProxy = new StandaloneToolsProxy(toolPort, allowNeedsInput: allowsHumanQuestions);
        if (member?.DecorateTools is { } decorate)
            toolsProxy = decorate(toolsProxy, effectiveAllowsHumanQuestions);

        var persistSession = ReadBool(aiNodeParams, "persist_session") ?? true;
        var sessionProxy = aiNodeId is not null ? (IAgentSessionProxy)new StandaloneSessionProxy(agentDefinition, aiNodeId, SessionKey(context), persistSession) : IAgentSessionProxy.Disabled;

        var channel = aiNodeId is not null ? AgentGraphResolver.TryResolveInteractionChannel(agentDefinition, aiNodeId) : null;
        var preparedHumanLoopContext = HilWaitProxy.PrepareHumanLoopContext(channel, interactionStore, effectiveAllowsHumanQuestions);

        // Suspend + resume instead of blocking. Without a parent run (standalone) PostRunAsync skips the parent-run
        // state update; StartAgentRunAsync already handles WaitingForInput via ExecutorResult.Suspended.
        var hilProxy =
            HilWaitProxy.TryCreate(channel, context, preparedHumanLoopContext, agentDefinition.Id, interactionStore, eventBus, member?.RunRepository, member?.ParentRunId, allowsHumanQuestions: effectiveAllowsHumanQuestions, logger: logger, memberDisplayName: member?.DisplayName)
            ?? IAgentHilProxy.Disabled;

        return (toolsProxy, sessionProxy, hilProxy, StandaloneTraceProxy.Instance, preparedHumanLoopContext);
    }

    /// <summary>
    /// Builds an <see cref="IAgentDefinitionResolver"/> for the given agent entity.
    /// </summary>
    public static IAgentDefinitionResolver BuildResolver(AgentEntity agentDefinition, AgentHost agentHost, IRunEventBus? eventBus, IAgentTelemetryEmitter? telemetryEmitter, Agentivity.Infrastructure.Repository.IRepository<RunContext, RunRecord>? runRepository = null, string? registryRunId = null) =>
        new AgentDefinitionResolver(agentDefinition, agentHost, eventBus, telemetryEmitter, runRepository, registryRunId);

    /// <summary>
    /// Session key for the agent's conversation store. An explicit <see cref="RunContext.SessionId"/>
    /// wins (members of a larger execution set their own, e.g. a team's <c>team-{runId}-{position}</c>);
    /// otherwise the standalone default <c>standalone-{runId}</c> (where RunId == ExecutionId,
    /// so the conversation spans the whole execution).
    /// </summary>
    private static string SessionKey(RunContext context) => context.SessionId ?? $"standalone-{context.RunId}";

    private static (string? aiNodeId, JsonElement? aiNodeParams) ResolveAiNode(AgentEntity agentDefinition)
    {
        var node = agentDefinition.Graph.Nodes.FirstOrDefault(n => string.Equals(n.NodeType, AiNodeType, StringComparison.OrdinalIgnoreCase));
        var aiNodeId = node?.Id;
        var aiNodeParams = node?.Parameters is { ValueKind: JsonValueKind.Object } p ? (JsonElement?)p : null;
        return (aiNodeId, aiNodeParams);
    }

    private static bool? ReadBool(JsonElement? el, string key)
    {
        if (el is null)
            return null;
        if (!el.Value.TryGetProperty(key, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.True ? true
            : prop.ValueKind == JsonValueKind.False ? false
            : null;
    }
}
