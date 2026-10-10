using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Agentic.Agent.Steps;

/// <summary>
/// What a step needs to decide whether/how to run — the run's immutable config, its mutable
/// state, and (for decision-style steps) the turn response being evaluated.
/// </summary>
public sealed class AgentStepContext(AgentConfig config, AgentRunState state, int iteration, RunContext? runContext = null)
{
    public AgentConfig Config { get; } = config;
    public AgentRunState State { get; } = state;
    public int Iteration { get; } = iteration;

    /// <summary>
    /// This run's <see cref="RunContext"/> — the single channel for "everything relevant said so
    /// far in this scope" (<see cref="RunContext.LastTranscriptSummary"/>), resolving to this
    /// run's own private scope for a standalone/workflow agent, or to the team's shared scope for
    /// a team member. Null only in tests that construct a step context directly without a run.
    /// </summary>
    public RunContext? RunContext { get; } = runContext;

    /// <summary>
    /// The turn response a decision step is being asked to react to — null when this context is
    /// driving a step that has nothing to react to yet (e.g. the main turn itself).
    /// </summary>
    public LlmConversationResponse? TurnResponse { get; init; }

    /// <summary>
    /// The main turn's own drafted text, to remember as <see cref="AgentRunState.PendingTurnDraft"/>
    /// if the decision step succeeds. Left null when the caller doesn't want this recorded (the
    /// forced follow-up in team repeat-of-previous-turn detection never has — see
    /// <c>AgentManifest.ApplyHumanLoopDecisionAsync</c>).
    /// </summary>
    public string? MainTurnDraft { get; init; }

    /// <summary>
    /// True when the decision step runs AFTER the turn's own <c>send</c>-mode human-loop calls have
    /// been executed and delivered (see <c>AgentManifest.TryFinalizeTurnAsync</c>), rather than in
    /// place of a turn that ended in plain text. The distinction is which turns owe a decision
    /// (<see cref="Implementations.AgentNextStepDecisionStep.ShouldRun"/>) and what it may
    /// choose from (no <c>needs_input</c>: whatever was sent is already delivered).
    /// </summary>
    public bool AfterDeliveredSends { get; init; }

    /// <summary>
    /// Index into <see cref="AgentRunState.Messages"/> where the live/summarizable window begins
    /// — <see cref="SessionContext.TranscriptUpToIndex"/>, but never before the leading system
    /// message (runtime instructions, never conversation history to summarize or drop), and never
    /// on an orphaned tool_result. Shared by <see cref="AgentStep.ResolveMessages"/>'s default (the
    /// windowed view every other step gets for free) and <c>SummaryStep</c> (which computes its own
    /// delta from the same boundary).
    /// <para>
    /// <see cref="SessionContext.TranscriptUpToIndex"/> is a plain list index, but
    /// <see cref="AgentRunState.Messages"/> isn't purely append-only: HIL resume
    /// (<c>HilWaitProxy.TryResumeAsync</c>) removes a range from the pending marker onward on every
    /// resume, independent of summarization. If that happens after the index has already advanced
    /// past that point, the index can end up pointing mid tool_use/tool_result pair once new
    /// messages are appended post-resume — confirmed in production (Anthropic rejected a
    /// tool_result with no matching tool_use in the previous message). Rather than trying to keep
    /// the index perfectly in sync with every mutation elsewhere in the codebase, this self-heals
    /// at read time: if the candidate start lands on a tool_result, walk backward to the message
    /// that opened it.
    /// </para>
    /// </summary>
    public int LiveWindowStartIndex
    {
        get
        {
            var messages = State.Messages;
            var hasLeadingSystemMessage = messages.Count > 0 && string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase);
            var start = Math.Clamp(State.SessionContext.TranscriptUpToIndex, hasLeadingSystemMessage ? 1 : 0, messages.Count);

            while (start > 0 && start < messages.Count && !string.IsNullOrWhiteSpace(messages[start].ToolCallId))
                start--;

            return start;
        }
    }
}
