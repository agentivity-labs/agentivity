using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Proxy for session management operations during an agent execution.
/// Abstracts loading and persisting conversation history so that
/// <c>AgentExecutable</c> is agnostic of the session store backend
/// (workflow resource port, standalone store, team context, etc.).
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><see cref="LoadAsync"/> — called once before <c>AgentHost.RunAsync</c>.</item>
///   <item><see cref="SaveAsync"/> — called once after <c>AgentHost.RunAsync</c>.</item>
/// </list>
/// </para>
/// </summary>
public interface IAgentSessionProxy
{
    /// <summary>
    /// Loads the session context for this execution.
    /// Returns <see cref="SessionContext.Disabled"/> when no session resource is configured.
    /// Implementations may have pre-run side effects (e.g. restoring remote MCP session state,
    /// restoring clarification ledger for HIL resume).
    /// </summary>
    ValueTask<SessionContext> LoadAsync(CancellationToken ct);

    /// <summary>
    /// Persists the session state after execution.
    /// No-op when <see cref="SessionContext.Enabled"/> is <see langword="false"/>
    /// or when the implementation is configured not to persist.
    /// </summary>
    /// <returns>Number of messages persisted (0 when session is disabled or persistSession = false).</returns>
    ValueTask<int> SaveAsync(SessionContext sessionContext, IReadOnlyList<LlmChatMessage> finalMessages, string prompt, string finalResponse, string finishReason, int executedIterations, string memoryContext, CancellationToken ct);

    /// <summary>
    /// A no-op proxy for contexts where session persistence is not supported
    /// (e.g. ephemeral team steps, programmatic calls without a session store).
    /// </summary>
    static IAgentSessionProxy Disabled { get; } = new DisabledSessionProxy();

    private sealed class DisabledSessionProxy : IAgentSessionProxy
    {
        public ValueTask<SessionContext> LoadAsync(CancellationToken ct) => ValueTask.FromResult(SessionContext.Disabled);

        public ValueTask<int> SaveAsync(SessionContext sessionContext, IReadOnlyList<LlmChatMessage> finalMessages, string prompt, string finalResponse, string finishReason, int executedIterations, string memoryContext, CancellationToken ct) => ValueTask.FromResult(0);
    }
}
