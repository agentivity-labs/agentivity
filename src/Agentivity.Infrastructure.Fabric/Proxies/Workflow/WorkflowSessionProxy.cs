using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Workflow implementation of <see cref="IAgentSessionProxy"/>.
/// Resolves the <c>session</c> resource node and loads/persists the <see cref="SessionContext"/>,
/// mirroring <c>StandaloneSessionProxy</c> (the standalone path).
/// <para>
/// <see cref="LoadAsync"/> also applies pre-run side effects that are
/// workflow-specific: remote MCP session state restoration and clarification
/// ledger restoration for HIL resume.
/// </para>
/// </summary>
public sealed class WorkflowSessionProxy : IAgentSessionProxy
{
    private readonly NodeConfig _config;
    private readonly WorkflowRunServices _services;
    private readonly string? _sessionId;
    private readonly string? _sessionScopeHint;
    private readonly bool _restoreSession;
    private readonly bool _persistSession;
    private SessionContext? _loaded;

    public WorkflowSessionProxy(NodeConfig config, WorkflowRunServices services, string? sessionId, string? sessionScopeHint, bool restoreSession, bool persistSession)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _sessionId = sessionId;
        _sessionScopeHint = sessionScopeHint;
        _restoreSession = restoreSession;
        _persistSession = persistSession;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Memoized — the underlying load and its side effects (remote MCP session restore,
    /// clarification ledger restore) run at most once per proxy instance, even if
    /// <see cref="LoadAsync"/> is called multiple times during a single node execution
    /// (e.g. once for HIL proxy construction and once by <c>AgentExecutable.ExecuteAgentAsync</c>).
    /// </remarks>
    public async ValueTask<SessionContext> LoadAsync(CancellationToken ct)
    {
        if (_loaded is not null)
            return _loaded;

        var sessionContext = await LoadSessionContextAsync(_config, _services, _sessionId, _sessionScopeHint, _restoreSession, ct).ConfigureAwait(false);

        // Workflow-specific side effects: must happen after loading, before execution.
        RestoreRemoteMcpSessionState(_config, _services, sessionContext);
        HumanLoopConversationContextHelper.RestoreClarificationLedger(_services.RunId, sessionContext.State.Metadata);

        _loaded = sessionContext;
        return sessionContext;
    }

    /// <inheritdoc />
    public async ValueTask<int> SaveAsync(SessionContext sessionContext, IReadOnlyList<LlmChatMessage> finalMessages, string prompt, string finalResponse, string finishReason, int executedIterations, CancellationToken ct)
    {
        if (!sessionContext.Enabled)
        {
            return 0;
        }

        sessionContext.State.Metadata["agent_node_id"] = _config.Node.Id;
        sessionContext.State.Metadata["last_prompt"] = prompt;
        sessionContext.State.Metadata["last_response"] = finalResponse;
        sessionContext.State.Metadata["last_finish_reason"] = finishReason;
        sessionContext.State.Metadata["last_iterations"] = executedIterations;
        sessionContext.State.Metadata["updated_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
        sessionContext.State.Metadata.Remove(HumanLoopHelper.PendingHumanReviewMetadataKey);
        HumanLoopConversationContextHelper.ClearClarificationLedger(_services.RunId);
        sessionContext.State.Metadata.Remove(HumanLoopConversationContextHelper.HumanLoopClarificationsMetadataKey);
        CaptureRemoteMcpSessionState(_config, _services, sessionContext);

        var persistedMessages = await SaveSessionStateAsync(_config, _services, sessionContext, finalMessages, _persistSession, ct).ConfigureAwait(false);

        return persistedMessages.Count;
    }

    // ── Session load/persist core ───────────────────────────────────────────────

    /// <summary>
    /// The session an agent node call uses. An explicit <c>session_id</c> is the author's choice to share a
    /// conversation, and is used exactly as given — across nodes, calls and runs. Otherwise each call of a
    /// node gets its own session: <c>run-&lt;RunId&gt;-&lt;node&gt;</c> for a node that runs once, with the call
    /// number appended (<c>…-2</c>) when the run calls the same node more than once (the iterations of a loop),
    /// so one iteration never inherits what an earlier one said. A call that suspends and resumes keeps its
    /// number, hence its session.
    /// </summary>
    public static string ResolveSessionId(string? explicitSessionId, Guid runId, string nodeId, int? callNumber)
    {
        if (!string.IsNullOrWhiteSpace(explicitSessionId))
        {
            return explicitSessionId.Trim();
        }

        var generated = $"{AgentRuntimeKeys.GeneratedSessionIdPrefix}-{runId:N}-{nodeId}";
        return callNumber is { } call ? $"{generated}-{call}" : generated;
    }

    private static async Task<SessionContext> LoadSessionContextAsync(NodeConfig config, WorkflowRunServices services, string? sessionIdInput, string? sessionScopeHint, bool restoreSession, CancellationToken cancellationToken)
    {
        var resolvedSession = ResourceResolver.TryResolveToolNode<IAgentSession>(config, "session", ResourcePortCategory.AIAgentSession);
        if (resolvedSession is null)
        {
            return SessionContext.Disabled;
        }

        var sessionId = ResolveSessionId(sessionIdInput, services.RunId, config.Node.Id, services.CallNumber);
        var openRequest = new AgentSessionOpenRequest(sessionId, config.Node.Id, sessionScopeHint);
        var session = await resolvedSession.Value.Tool.OpenAsync(resolvedSession.Value.Binding, openRequest, cancellationToken).ConfigureAwait(false);

        if (!restoreSession)
        {
            return new SessionContext(resolvedSession.Value.Tool, resolvedSession.Value.Binding, sessionId, session.Scope, false, [], session);
        }

        return new SessionContext(
            resolvedSession.Value.Tool,
            resolvedSession.Value.Binding,
            sessionId,
            session.Scope,
            session.Messages.Count > 0,
            session.Messages.Select(LlmChatMessageCloner.CloneMessage).ToList(),
            session
        );
    }

    /// <summary>
    /// Persists the given messages to the session store (if enabled and <paramref name="persistSession"/>).
    /// Used both by <see cref="SaveAsync"/> and by <see cref="WorkflowHilProxy.WaitForHumanReviewAsync"/>
    /// to checkpoint state before suspending for human review.
    /// </summary>
    internal static async Task<List<LlmChatMessage>> SaveSessionStateAsync(NodeConfig config, WorkflowRunServices services, SessionContext sessionContext, IReadOnlyList<LlmChatMessage> messages, bool persistSession, CancellationToken cancellationToken)
    {
        var persistedMessages = messages.Where(message => !string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase)).Select(LlmChatMessageCloner.CloneMessage).ToList();

        if (!sessionContext.Enabled)
        {
            return persistedMessages;
        }

        if (persistSession)
        {
            await sessionContext
                .Session!.SaveAsync(
                    sessionContext.Binding!,
                    new AgentSessionState(
                        sessionContext.SessionId,
                        sessionContext.Scope,
                        persistedMessages,
                        SessionToolStateHelper.CloneToolState(sessionContext.State.ToolState),
                        new Dictionary<string, object?>(sessionContext.State.Metadata, StringComparer.OrdinalIgnoreCase),
                        DateTimeOffset.UtcNow,
                        sessionContext.TranscriptUpToIndex
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            services.PublishStatus($"Persisted AI agent session '{sessionContext.SessionId}' ({sessionContext.Scope}) with {persistedMessages.Count} message(s).");
        }

        return persistedMessages;
    }

    private static void RestoreRemoteMcpSessionState(NodeConfig config, WorkflowRunServices services, SessionContext sessionContext)
    {
        if (!sessionContext.Enabled)
        {
            return;
        }

        if (!sessionContext.State.Metadata.TryGetValue(AgentRuntimeKeys.RemoteMcpSessionsMetadataKey, out var rawValue) || rawValue is null)
        {
            return;
        }

        JsonObject? persistedSessions = rawValue switch
        {
            JsonObject jsonObject => (JsonObject)jsonObject.DeepClone(),
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Object => JsonNode.Parse(jsonElement.GetRawText()) as JsonObject,
            string jsonText when !string.IsNullOrWhiteSpace(jsonText) => JsonNode.Parse(jsonText) as JsonObject,
            _ => null,
        };

        if (persistedSessions is null)
        {
            return;
        }

        foreach (var (_, binding) in ResourceResolver.ResolveToolNodes<IAgentTool>(config, "tools", ResourcePortCategory.AIAgentTool))
        {
            if (!string.Equals(binding.SourceNode.NodeType, "ai.tools.mcp.server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (persistedSessions[binding.SourceNodeId]?.GetValue<string>() is not { Length: > 0 } sessionId)
            {
                continue;
            }

            binding.SetStateValue(HttpMcpToolBridge.BuildSessionStateKey(binding), sessionId);
        }
    }

    /// <summary>
    /// Used both by <see cref="SaveAsync"/> and by <see cref="WorkflowHilProxy.WaitForHumanReviewAsync"/>
    /// to capture remote MCP session state before suspending for human review.
    /// </summary>
    internal static void CaptureRemoteMcpSessionState(NodeConfig config, WorkflowRunServices services, SessionContext sessionContext)
    {
        if (!sessionContext.Enabled)
        {
            return;
        }

        var remoteSessions = new JsonObject();

        foreach (var (_, binding) in ResourceResolver.ResolveToolNodes<IAgentTool>(config, "tools", ResourcePortCategory.AIAgentTool))
        {
            if (!string.Equals(binding.SourceNode.NodeType, "ai.tools.mcp.server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (binding.GetStateValue(HttpMcpToolBridge.BuildSessionStateKey(binding)) is not string sessionId || string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            remoteSessions[binding.SourceNodeId] = sessionId;
        }

        if (remoteSessions.Count > 0)
        {
            sessionContext.State.Metadata[AgentRuntimeKeys.RemoteMcpSessionsMetadataKey] = remoteSessions;
        }
    }
}
