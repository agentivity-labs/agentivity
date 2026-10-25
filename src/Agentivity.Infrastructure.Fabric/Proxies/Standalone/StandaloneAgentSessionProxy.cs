using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Standalone implementation of <see cref="IAgentSessionPort"/>.
/// Delegates load/persist/checkpoint to <see cref="IAgentSession"/> resolved from
/// <see cref="AgentEntity.Graph"/> by <see cref="AgentGraphResolver"/>.
/// <para>
/// <see cref="CheckpointAsync"/> persists the current message state so that
/// mid-execution interruptions (crash, timeout, HIL) can resume from the latest checkpoint.
/// </para>
/// </summary>
internal sealed class StandaloneAgentSessionProxy : IAgentSessionPort
{
    private readonly IAgentSession _session;
    private readonly ResourceBinding _binding;
    private readonly string _sessionId;
    private readonly string _scope;
    private readonly IReadOnlyList<LlmChatMessage> _restoredMessages;
    private readonly bool _persistSession;

    public StandaloneAgentSessionProxy(IAgentSession session, ResourceBinding binding, string sessionId, string scope, IReadOnlyList<LlmChatMessage> restoredMessages, bool persistSession)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _restoredMessages = restoredMessages ?? throw new ArgumentNullException(nameof(restoredMessages));
        _persistSession = persistSession;
    }

    // ── IRunSessionPort ───────────────────────────────────────────────────────

    public string? SessionId => _sessionId;
    public bool IsEnabled => true;

    // ── IAgentSessionPort ─────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<LlmChatMessage>> LoadMessagesAsync(bool restore, CancellationToken ct)
    {
        if (!restore)
            return new ValueTask<IReadOnlyList<LlmChatMessage>>([]);

        return new ValueTask<IReadOnlyList<LlmChatMessage>>(_restoredMessages);
    }

    public ValueTask PersistAsync(IReadOnlyList<LlmChatMessage> messages, int transcriptUpToIndex, CancellationToken ct) => _persistSession ? SaveCoreAsync(messages, transcriptUpToIndex, ct) : ValueTask.CompletedTask;

    public ValueTask CheckpointAsync(IReadOnlyList<LlmChatMessage> messages, int transcriptUpToIndex, CancellationToken ct) => _persistSession ? SaveCoreAsync(messages, transcriptUpToIndex, ct) : ValueTask.CompletedTask;

    public void ApplyCompaction(List<LlmChatMessage> messages)
    {
        var compacted = AgentSessionCompaction.NormalizeForPersistence(_binding, new Agentivity.Infrastructure.Workflows.Resources.AgentSessionState(_sessionId, _scope, messages, new Dictionary<string, System.Text.Json.Nodes.JsonObject>(), new Dictionary<string, object?>(), DateTimeOffset.UtcNow));

        if (compacted.Messages.Count < messages.Count)
        {
            messages.Clear();
            messages.AddRange(compacted.Messages);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async ValueTask SaveCoreAsync(IReadOnlyList<LlmChatMessage> messages, int transcriptUpToIndex, CancellationToken ct)
    {
        var toSave = messages.Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)).Select(LlmChatMessageCloner.CloneMessage).ToList();

        await _session
            .SaveAsync(
                _binding,
                new Agentivity.Infrastructure.Workflows.Resources.AgentSessionState(_sessionId, _scope, toSave, new Dictionary<string, System.Text.Json.Nodes.JsonObject>(), new Dictionary<string, object?> { ["updated_at_utc"] = DateTimeOffset.UtcNow.ToString("O") }, DateTimeOffset.UtcNow, transcriptUpToIndex),
                ct
            )
            .ConfigureAwait(false);
    }

    // ── Static factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="StandaloneAgentSessionProxy"/> by resolving the session node
    /// connected to <paramref name="aiNodeId"/> in the agent graph.
    /// Returns null when no session node is connected.
    /// <paramref name="sessionId"/> is the explicit session key — the caller owns the keying
    /// policy (<c>standalone-{runId}</c> for standalone agents, <c>team-{teamRunId}-{position}</c>
    /// for team members via <c>RunContext.SessionId</c>).
    /// </summary>
    public static async ValueTask<(StandaloneAgentSessionProxy Port, SessionContext Context)?> TryCreateAsync(AgentEntity agent, string aiNodeId, string sessionId, bool persistSession, CancellationToken ct)
    {
        var resolved = AgentGraphResolver.ResolveSession(agent, aiNodeId);
        if (resolved is null)
            return null;

        var (session, binding) = resolved.Value;
        var openRequest = new Agentivity.Infrastructure.Workflows.Resources.AgentSessionOpenRequest(sessionId, aiNodeId);
        var state = await session.OpenAsync(binding, openRequest, ct).ConfigureAwait(false);

        var restoredMessages = state.Messages.Count > 0 ? (IReadOnlyList<LlmChatMessage>)state.Messages.Select(LlmChatMessageCloner.CloneMessage).ToList() : [];

        var port = new StandaloneAgentSessionProxy(session, binding, sessionId, state.Scope, restoredMessages, persistSession);

        // Build the full SessionContext — a higher-level aggregate distinct from the
        // IAgentSessionPort I/O boundary above — the SAME way
        // WorkflowAgentSessionCoordinator.LoadSessionContextAsync builds it: Restored /
        // RestoredMessages / State all populated from the resumed AgentSessionState, instead of
        // SessionContext.Disabled (characterized gap, Story 2). The rolling summary itself now
        // lives on RunContext.LastTranscriptSummary, not here — see TranscriptScope.
        var restored = state.Messages.Count > 0;
        var context = new SessionContext(session, binding, sessionId, state.Scope, restored, restoredMessages, state);

        return (port, context);
    }
}
