using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Standalone implementation of <see cref="IAgentSessionProxy"/>.
/// Resolves the session from the agent's graph, loads conversation history,
/// and persists the session after execution.
/// <para>
/// In the standalone path, the session store is an <see cref="IAgentSession"/> node
/// connected to the <c>ai.node</c> in the <see cref="AgentEntity.Graph"/>,
/// resolved by <see cref="StandaloneAgentSessionProxy.TryCreateAsync"/>.
/// </para>
/// </summary>
internal sealed class StandaloneSessionProxy : IAgentSessionProxy
{
    private readonly AgentEntity _agent;
    private readonly string _aiNodeId;
    private readonly string _sessionId;
    private readonly bool _persistSession;

    // Populated by LoadAsync — holds the underlying port for SaveAsync.
    private StandaloneAgentSessionProxy? _sessionPort;

    public StandaloneSessionProxy(AgentEntity agent, string aiNodeId, string sessionId, bool persistSession)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _aiNodeId = aiNodeId ?? throw new ArgumentNullException(nameof(aiNodeId));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _persistSession = persistSession;
    }

    /// <inheritdoc />
    public async ValueTask<SessionContext> LoadAsync(CancellationToken ct)
    {
        var resolution = await StandaloneAgentSessionProxy.TryCreateAsync(_agent, _aiNodeId, _sessionId, _persistSession, ct).ConfigureAwait(false);

        if (resolution is null)
            return SessionContext.Disabled;

        _sessionPort = resolution.Value.Port;
        var sessionContext = resolution.Value.Context;

        // Ensure restored messages are loaded and reflected in the context.
        var restoredMessages = await _sessionPort.LoadMessagesAsync(restore: true, ct).ConfigureAwait(false);

        if (restoredMessages.Count > 0 && sessionContext.RestoredMessages.Count == 0)
            sessionContext = sessionContext with { RestoredMessages = restoredMessages };

        return sessionContext;
    }

    /// <inheritdoc />
    public async ValueTask<int> SaveAsync(SessionContext sessionContext, IReadOnlyList<LlmChatMessage> finalMessages, string prompt, string finalResponse, string finishReason, int executedIterations, string memoryContext, CancellationToken ct)
    {
        if (_sessionPort is null || !sessionContext.Enabled)
            return 0;

        await _sessionPort.PersistAsync(finalMessages, sessionContext.TranscriptUpToIndex, ct).ConfigureAwait(false);

        // Return number of non-system messages persisted.
        return finalMessages.Count(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
    }
}
