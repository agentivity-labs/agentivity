using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.Steps.Implementations;

/// <summary>
/// Rolls the unsummarized tail of <see cref="AgentRunState.Messages"/> into
/// <see cref="Agentivity.Infrastructure.Execution.Runs.RunContext.LastTranscriptSummary"/> —
/// plain text, no structure, no tool calls. Never mutates <see cref="AgentRunState.Messages"/>
/// itself; only <see cref="SessionContext.TranscriptUpToIndex"/> moves (that index stays on the
/// session, not the run: it's an offset into THIS turn's own message list, which has no
/// consistent meaning inside a scope that may be shared across many team members' distinct
/// message lists). The summary text itself lives on the resolved <c>TranscriptScope</c> — this
/// run's own private scope for a standalone/workflow agent, or the team's shared scope for a
/// team member — so a team member's own turns fold into the same scope every other member reads.
/// See <c>.claude/EPIC-agent-transcript-summary.md</c> for the original design.
/// <para>
/// The recipe (safe-consume-count, prompt construction) lives in <see cref="AgentSessionCompaction"/>,
/// shared with the team-handoff trigger — this step only wraps it in the standard <c>AgentStep</c>
/// pipeline so it uses the agent's own resolved LLM (via the <c>invokeLlm</c> delegate) instead of
/// a raw provider/binding.
/// </para>
/// <para>
/// Must be constructed fresh per call (like <c>MissedAskHumanFallbackStep</c>, never a shared
/// static instance like <c>AgentNextStepDecisionStep</c>): <see cref="ShouldRun"/> computes how
/// much of the delta this run will consume and stashes it on the instance for
/// <see cref="ResolveMessages"/>/<see cref="ApplyAsync"/> to use — that's per-call state, unsafe
/// to share across concurrent runs.
/// </para>
/// </summary>
public sealed class SummaryStep : AgentStep<string>
{
    /// <summary>
    /// Rough token estimate threshold for the unsummarized delta — <c>characters / 4</c>, no
    /// tokenizer. Same default as LangChain's <c>ConversationSummaryBufferMemory.max_token_limit</c>;
    /// a reasonable starting point, not a precisely-tuned value.
    /// </summary>
    public const int DeltaTokenThreshold = 2000;

    private IReadOnlyList<LlmChatMessage> _consumed = [];

    public override string Name => "summary";

    public override bool ShouldRun(AgentStepContext context)
    {
        // Session's own compaction_mode gates the rolling summary (defaults to "summary" —
        // "none" is the raw-mode escape hatch for A/B comparison, see
        // .claude/EPIC-session-summary-unification.md). No session/binding configured still
        // defaults to on: bounding what's sent to the LLM this turn is useful even when nothing
        // persists across turns.
        var compactionMode = context.State.SessionContext.Binding?.GetString("compaction_mode")?.Trim().ToLowerInvariant() ?? "summary";
        if (string.Equals(compactionMode, "none", StringComparison.Ordinal))
            return false;

        var delta = GetDelta(context);
        if (AgentSessionCompaction.EstimateTokens(delta) <= DeltaTokenThreshold)
            return false;

        var safeCount = AgentSessionCompaction.ResolveSafeConsumeCount(delta, DeltaTokenThreshold);
        if (safeCount == 0)
            // Over budget, but there's no point in the delta yet where every tool_use so far has
            // its tool_result — e.g. a tool call is still mid-flight. Wait for the next safe
            // boundary rather than risk cutting a pair; this iteration's ShouldRun will be asked
            // again next loop turn.
            return false;

        _consumed = [.. delta.Take(safeCount)];
        return true;
    }

    public override IReadOnlyList<LlmChatMessage> ResolveMessages(AgentStepContext context) =>
        [new LlmChatMessage("user", AgentSessionCompaction.BuildSummaryPrompt(context.RunContext?.LastTranscriptSummary, _consumed))];

    public override IReadOnlyList<JsonObject>? ResolveTools(AgentStepContext context) => null;

    public override LlmConversationOptions BuildOptions(AgentStepContext context) =>
        new(Temperature: context.Config.Temperature, MaxTokens: AgentSessionCompaction.DefaultSummaryMaxTokens, ToolChoiceMode: LlmToolChoiceMode.None, AllowParallelToolCalls: false);

    public override ValueTask<string> ApplyAsync(LlmConversationResponse response, AgentStepContext context, CancellationToken cancellationToken)
    {
        var updatedTranscript = (response.Content ?? string.Empty).Trim();
        if (context.RunContext is not null)
            context.RunContext.LastTranscriptSummary = updatedTranscript;
        context.State.SessionContext.TranscriptUpToIndex += _consumed.Count;
        return ValueTask.FromResult(updatedTranscript);
    }

    private static IReadOnlyList<LlmChatMessage> GetDelta(AgentStepContext context) =>
        [.. context.State.Messages.Skip(context.LiveWindowStartIndex)];
}
