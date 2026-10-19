using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.Steps.Implementations;

/// <summary>
/// The turn-conclusion decision step (<c>agent_next_step</c>) — a single follow-up call,
/// restricted to the routing tool alone, that makes the agent state explicitly what happens
/// next: "done", "needs_input", or (team members only) a real handoff target. Used both as the
/// generic end-of-turn step (<c>AgentManifest.ResolveTurnConclusionAsync</c>) and as a forced
/// follow-up when a team member's free-text turn needs an explicit routing decision
/// (<c>AgentManifest.ApplyHumanLoopDecisionAsync</c>) — both call sites previously duplicated
/// this exact token-budget/tool-restriction/apply logic; this is the single implementation.
/// </summary>
public sealed class AgentNextStepDecisionStep : AgentStep<LlmConversationResponse>
{
    /// <summary>
    /// Token budget for this call. Its whole output is a structured tool call (an action, a
    /// target, a short message), never free text — so the agent's own (often much larger)
    /// <see cref="AgentConfig.MaxTokens"/> would be the wrong budget to reuse.
    /// <para>
    /// This is a safety net, not a content constraint: this call already sets
    /// <c>ToolChoiceMode.Required</c>, which *asks* the model to always emit the tool call — but
    /// that's an instruction, not an enforced guarantee, and some backends/models don't reliably
    /// honor it (see <see cref="ApplyAsync"/>'s graceful-degradation fallback). Confirmed in
    /// production against LM Studio on <em>two different models</em> (Qwen2.5-14B and Qwen3-14B —
    /// swapping models did not fix this): despite <c>required</c>, the model abandoned the
    /// structured call and rambled in free text instead, consuming a full 4000-token budget
    /// (~90+ seconds) before giving up. The cap still bounds that failure mode, just more
    /// generously: it never truncates a real decision, and current models have output ceilings
    /// far above this value.
    /// </para>
    /// <para>
    /// Do not set this to <see langword="null"/> to "remove the limit": an absent <c>max_tokens</c>
    /// does not mean unbounded-but-safe — it means the provider's own default applies instead
    /// (often the full context window), which is a worse, less predictable ceiling than this one.
    /// </para>
    /// <para>
    /// This only bounds this call's own output — it does not limit what this call can read:
    /// <see cref="ResolveMessages"/> is not overridden, so it sees the exact same full
    /// conversation (system prompt, team summary, transcript) as the turn that preceded it.
    /// </para>
    /// </summary>
    public const int RoutingDecisionMaxTokens = 10000;

    public override string Name => "agent_next_step_decision";

    /// <summary>
    /// A decision is owed after a turn that only communicated — plain text, or "send"-mode
    /// human-loop calls (e.g. <c>interact_with_human</c> with <c>mode: "send"</c>): a send is
    /// communication, not team-state progress. Treating "any tool call happened" as "real work
    /// happened, no decision needed yet" let a model narrate fake delegation turn after turn
    /// ("Dispatching the Lodging specialist...", "Dispatching the Flights specialist...") via
    /// repeated sends, without ever calling <c>agent_next_step</c> for any of them — confirmed in
    /// production against a ManagerLed team where 8 of 9 announced dispatches were never real. A
    /// genuine <c>mode: "ask"</c> call is exempted: it already suspends the run for a real reply,
    /// so no decision is needed yet either way.
    /// <para>
    /// The two cases run at different moments, told apart by
    /// <see cref="AgentStepContext.AfterDeliveredSends"/>. A text-only turn has nothing left to
    /// execute, so its decision runs immediately (<c>AgentManifest.ResolveTurnConclusionAsync</c>).
    /// A turn with sends must have those sends EXECUTED first — the decision is a separate call
    /// that follows the delivery, never a replacement for it: this step's response is what the
    /// runtime executes next, so running it in place of a turn that carried a send silently threw
    /// that send away (confirmed on every log since Sep 13: 21 of 21 turns pairing text with a
    /// send lost the send — 6 of them a real widget) and left the decision judging a message the
    /// agent had not finished.
    /// </para>
    /// </summary>
    public override bool ShouldRun(AgentStepContext context)
    {
        if (context.TurnResponse is not { } response || string.IsNullOrWhiteSpace(response.Content))
            return false;

        return context.AfterDeliveredSends
            ? response.ToolCalls.Count > 0 && response.ToolCalls.All(tc => IsSendOnlyHumanLoopCall(tc, context.Config))
            : response.ToolCalls.Count == 0;
    }

    private static bool IsSendOnlyHumanLoopCall(LlmToolCall toolCall, AgentConfig config)
    {
        if (!config.HumanLoopToolDefinitionsByName.ContainsKey(toolCall.Name))
            return false;

        var mode = HumanLoopHelper.GetToolArgumentString(toolCall.ArgumentsJson, "mode");
        return !string.Equals(mode?.Trim(), "ask", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The conversation as the turn saw it, followed — when it ends with the agent's own reply — by a short request from the
    /// user's side: recent models refuse a conversation that ends with their own message ("assistant prefill"). Only this call
    /// sends it; the conversation kept is unchanged.
    /// </summary>
    public override IReadOnlyList<LlmChatMessage> ResolveMessages(AgentStepContext context)
    {
        var messages = base.ResolveMessages(context);
        if (messages.Count == 0 || !string.Equals(messages[^1].Role, "assistant", StringComparison.OrdinalIgnoreCase))
            return messages;
        return [.. messages, new LlmChatMessage("user", "Your reply above is complete. Now say what happens next with agent_next_step.")];
    }

    public override IReadOnlyList<JsonObject>? ResolveTools(AgentStepContext context) =>
        context.Config.TurnConclusionTool is { } tool ? [(context.AfterDeliveredSends ? tool.WithoutContentDependentActions() : tool).Definition] : null;

    public override LlmConversationOptions BuildOptions(AgentStepContext context) =>
        new(Temperature: context.Config.Temperature, MaxTokens: RoutingDecisionMaxTokens, ToolChoiceMode: LlmToolChoiceMode.Required, AllowParallelToolCalls: false);

    public override ValueTask<LlmConversationResponse> ApplyAsync(LlmConversationResponse response, AgentStepContext context, CancellationToken cancellationToken)
    {
        // Graceful degradation if the model still doesn't produce the call (some backends don't
        // always honor tool_choice) — fall back to the turn's own original response rather than
        // getting stuck. Except after delivered sends: the original response carries those very
        // sends, already executed — handing it back would run them a second time, so the (empty)
        // decision response is returned as-is and the turn simply carries on without a decision.
        if (response.ToolCalls.Count == 0)
            return ValueTask.FromResult(context.AfterDeliveredSends ? response : context.TurnResponse!);

        if (context.MainTurnDraft is not null)
            context.State.PendingTurnDraft = context.MainTurnDraft;

        return ValueTask.FromResult(response);
    }
}
