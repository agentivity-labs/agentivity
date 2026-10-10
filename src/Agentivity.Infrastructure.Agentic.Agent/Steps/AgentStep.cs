using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.Steps;

/// <summary>
/// The non-generic surface of a step — everything except how its LLM response gets interpreted
/// (see <see cref="AgentStep{TResult}"/> for that). Split out so callers that only need to check
/// whether a step applies (<see cref="ShouldRun"/>) don't need to know its result type.
/// </summary>
public abstract class AgentStep
{
    /// <summary>Stable name for tracing/logging — never shown to the user.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Whether this step should run now, given <paramref name="context"/>. The single source of
    /// truth for this step's trigger condition — callers ask this instead of recomputing their
    /// own version of it, so the condition lives in exactly one place.
    /// </summary>
    public abstract bool ShouldRun(AgentStepContext context);

    /// <summary>
    /// The message list for this step's LLM call. Defaults to the run's own running conversation,
    /// windowed — <see cref="AgentRunState.Messages"/> itself is never trimmed (see
    /// <c>SummaryStep</c>/<c>.claude/EPIC-agent-transcript-summary.md</c>), but what's actually
    /// sent is <see cref="Agentivity.Infrastructure.Execution.Runs.RunContext.LastTranscriptSummary"/>
    /// folded into the leading system message plus only the messages from
    /// <see cref="AgentStepContext.LiveWindowStartIndex"/> onward. Resolves to this run's own
    /// private scope for a standalone/workflow agent, or the team's shared scope for a team
    /// member — one channel, "everything relevant said so far in this scope," never a separate
    /// private-vs-team distinction. Until a summary has ever run, this is byte-for-byte the full
    /// conversation — zero behavior change for steps that don't override this. Override for a
    /// step that needs an isolated, self-contained call instead (e.g. a classifier prompt built
    /// from a single piece of text, unrelated to conversation history).
    /// </summary>
    public virtual IReadOnlyList<LlmChatMessage> ResolveMessages(AgentStepContext context)
    {
        var messages = context.State.Messages;
        var startIndex = context.LiveWindowStartIndex;
        var hasLeadingSystemMessage = startIndex > 0;
        var window = messages.Skip(startIndex).ToList();

        var transcript = context.RunContext?.LastTranscriptSummary;
        if (string.IsNullOrWhiteSpace(transcript))
        {
            if (hasLeadingSystemMessage)
                window.Insert(0, messages[0]);
            return window;
        }

        var transcriptBlock = $"Résumé de la conversation jusqu'ici :\n\n{transcript}";
        var systemContent = hasLeadingSystemMessage ? $"{messages[0].Content}\n\n{transcriptBlock}" : transcriptBlock;
        window.Insert(0, new LlmChatMessage("system", systemContent));
        return window;
    }

    /// <summary>Tools offered for this step's LLM call — null/empty if it needs none.</summary>
    public abstract IReadOnlyList<JsonObject>? ResolveTools(AgentStepContext context);

    /// <summary>Options for this step's LLM call (token budget, tool_choice, temperature...).</summary>
    public abstract LlmConversationOptions BuildOptions(AgentStepContext context);
}

/// <summary>
/// A narrow, self-contained LLM call in an agent's turn pipeline — the main turn, the
/// <c>agent_next_step</c> decision, and (future) a summarization pass all share this shape: a
/// trigger condition, a restricted (or unrestricted) tool set, a token/options budget, and a way
/// to apply the result back onto the run's state. Concrete steps live in
/// <c>Steps/Implementations/</c>. See <c>.claude/EPIC-agent-step-system.md</c> for the design.
/// <para>
/// <typeparamref name="TResult"/> is what the step hands back to its caller — deliberately not
/// always the raw <see cref="LlmConversationResponse"/>: a step that exists to answer a specific
/// question (e.g. "did this need a human answer?") should return that answer directly, not make
/// its caller re-parse the response it already interpreted in <see cref="ApplyAsync"/>. If a
/// step's whole point IS to carry the response forward unmodified (the main turn, the routing
/// decision), <typeparamref name="TResult"/> is simply <see cref="LlmConversationResponse"/>.
/// </para>
/// <para>
/// <see cref="ExecuteAsync"/> is the fixed skeleton (resolve messages/tools/options, invoke,
/// apply) — subclasses only ever override the hooks, never the sequencing itself.
/// </para>
/// </summary>
public abstract class AgentStep<TResult> : AgentStep
{
    /// <summary>
    /// Applies the LLM's response to <paramref name="context"/>'s run state and returns what this
    /// step means to tell its caller — the fully interpreted result, not a value the caller still
    /// has to parse.
    /// </summary>
    public abstract ValueTask<TResult> ApplyAsync(LlmConversationResponse response, AgentStepContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Runs this step: resolve messages/tools/options, invoke <paramref name="invokeLlm"/>, apply
    /// the result. Not virtual — the sequencing is the one thing every step shares and none of
    /// them should be able to change. Callers are expected to check <see cref="AgentStep.ShouldRun"/>
    /// themselves before calling this — kept separate rather than folded in here so a caller can
    /// branch on the decision without paying for message/tool/option resolution it won't use.
    /// </summary>
    public async ValueTask<TResult> ExecuteAsync(AgentStepContext context, Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<JsonObject>?, LlmConversationOptions, CancellationToken, ValueTask<LlmConversationResponse>> invokeLlm, CancellationToken cancellationToken)
    {
        var messages = ResolveMessages(context);
        var tools = ResolveTools(context);
        var options = BuildOptions(context);
        var response = await invokeLlm(messages, tools, options, cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(response, context, cancellationToken).ConfigureAwait(false);
    }
}
