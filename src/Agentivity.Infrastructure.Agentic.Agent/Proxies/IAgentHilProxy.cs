using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Proxy for human-in-the-loop (HIL) concerns during agent preparation and execution.
/// Each execution context (workflow, team, standalone) provides its own implementation.
/// </summary>
public interface IAgentHilProxy
{
    /// <summary>Whether HIL is enabled for this run.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Whether this agent is allowed to wait for a human reply at all (see the ai.node's
    /// <c>allows_human_questions</c> argument). When <see langword="false"/>, HIL tool schemas
    /// must be built without an "ask"/wait choice, and callers must never upgrade a "send" into a
    /// suspend — see <c>AgentManifest.ExecuteToolsCoreAsync</c>'s <c>MissedAskHumanFallbackStep</c>
    /// gate. Defaults to <see langword="true"/> so contexts that don't care about this distinction
    /// (e.g. <see cref="Workflow.WorkflowHilProxy"/>) keep their existing behavior unchanged.
    /// </summary>
    bool AllowsHumanQuestions => true;

    /// <summary>Native HIL tool definitions to expose to the LLM. Empty when HIL is disabled.</summary>
    IReadOnlyList<JsonObject> ToolDefinitions { get; }

    /// <summary>
    /// Builds the human-loop system prompt layer injected into the initial messages.
    /// Returns an empty string when HIL is not applicable.
    /// </summary>
    string BuildSystemPrompt();

    /// <summary>
    /// Returns any pending human review stored in the session state (workflow HIL resume), or null.
    /// Used to decide whether the current user prompt should be appended to initial messages.
    /// </summary>
    PendingHumanReview? GetPendingReview(SessionContext sessionContext);

    /// <summary>
    /// Attempts to resume from a previously suspended HIL state.
    /// Returns <see cref="HumanReviewResumeState.None"/> when nothing to resume (standalone, or no prior suspension).
    /// May inject tool/assistant messages directly into <paramref name="messages"/>.
    /// </summary>
    ValueTask<HumanReviewResumeState> TryResumeAsync(SessionContext sessionContext, List<LlmChatMessage> messages, IReadOnlySet<string> reusableToolNames, CancellationToken ct);

    /// <summary>
    /// Suspends the agent run for human review. Only called when <see cref="IsEnabled"/> is <c>true</c>.
    /// Returns the opaque suspension token (e.g. HIL request id) when the proxy signals suspension
    /// to the caller (non-blocking proxies), or <see langword="null"/> when the proxy handles the
    /// response itself in-place (blocking proxies like <c>StandaloneHilProxy</c>).
    /// </summary>
    ValueTask<string?> WaitForHumanReviewAsync(AgentTraceMetadata traceMetadata, SessionContext sessionContext, IReadOnlyList<LlmChatMessage> messages, PendingHumanReview pendingReview, IReadOnlyList<AgentToolExecution> executedToolCalls, bool persistSession, CancellationToken cancellationToken);

    /// <summary>
    /// Dispatches a HIL tool call's content immediately without suspending the run — used when
    /// the call's resolved <see cref="PendingHumanReview.Operation"/> is
    /// <see cref="Agentivity.Infrastructure.Workflows.Interactions.HumanInteractionOperation.Send"/>
    /// (e.g. <c>interact_with_human</c> called with <c>mode: "send"</c>). Returns the tool result
    /// text to feed back to the LLM; the agent loop continues to the next iteration immediately.
    /// </summary>
    ValueTask<string> SendWithoutWaitingAsync(PendingHumanReview pendingReview, CancellationToken cancellationToken) => throw new NotSupportedException("Sending without waiting is not supported for this execution context.");

    /// <summary>
    /// Called by <see cref="AgentHost.RunAgentAsync"/> immediately after the session is persisted.
    /// Non-blocking proxies (e.g. <c>HilWaitProxy</c>) use this to dispatch the HIL
    /// notification to the channel AFTER the session marker is durably saved, ensuring the
    /// run can be resumed correctly before the user's reply arrives.
    /// Blocking proxies (e.g. <c>StandaloneHilProxy</c>) leave the default no-op.
    /// </summary>
    ValueTask PostRunAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>A no-op proxy for contexts where HIL is not supported.</summary>
    static IAgentHilProxy Disabled { get; } = new DisabledAgentHilProxy();

    private sealed class DisabledAgentHilProxy : IAgentHilProxy
    {
        public bool IsEnabled => false;
        public IReadOnlyList<JsonObject> ToolDefinitions => [];

        public string BuildSystemPrompt() => string.Empty;

        public PendingHumanReview? GetPendingReview(SessionContext sessionContext) => null;

        public ValueTask<HumanReviewResumeState> TryResumeAsync(SessionContext sessionContext, List<LlmChatMessage> messages, IReadOnlySet<string> reusableToolNames, CancellationToken ct) => ValueTask.FromResult(HumanReviewResumeState.None);

        public ValueTask<string?> WaitForHumanReviewAsync(
            AgentTraceMetadata traceMetadata,
            SessionContext sessionContext,
            IReadOnlyList<LlmChatMessage> messages,
            PendingHumanReview pendingReview,
            IReadOnlyList<AgentToolExecution> executedToolCalls,
            bool persistSession,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("HIL is disabled for this execution context.");
    }
}
