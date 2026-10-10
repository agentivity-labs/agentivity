using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.Steps.Implementations;

/// <summary>
/// Safety net for a "send" mode <c>interact_with_human</c> call (sent without waiting for a
/// reply) whose text content actually reads as a question — a missed "ask". Owns its full
/// decision end to end: <see cref="ShouldRun"/> resolves the send text from the tool call's own
/// arguments and gates on it being non-empty (no separate check needed at the call site), and
/// <see cref="ApplyAsync"/> parses the classifier's response and hands back the one thing its
/// caller actually needs — whether the pending review should be requalified to
/// <c>SendAndWait</c> — instead of the raw response for the caller to reinterpret.
/// <para>
/// An isolated, single-message classifier call, unrelated to the running conversation history —
/// overrides <see cref="ResolveMessages"/> instead of reusing <see cref="AgentStepContext.State"/>'s
/// messages. See the caller (the HIL tool-dispatch branch in
/// <c>AgentManifest.ExecuteToolsCoreAsync</c>) for why this exists: without it, the loop runs an
/// extra turn and the model narrates "I sent you a question…" instead of actually waiting for one.
/// </para>
/// </summary>
public sealed class MissedAskHumanFallbackStep(string toolCallArgumentsJson) : AgentStep<bool>
{
    private string? _sendText;

    public override string Name => "missed_ask_human_fallback";

    public override bool ShouldRun(AgentStepContext context)
    {
        _sendText = HumanLoopHelper.ResolveContentBlocksText(toolCallArgumentsJson);
        return !string.IsNullOrWhiteSpace(_sendText);
    }

    public override IReadOnlyList<LlmChatMessage> ResolveMessages(AgentStepContext context) =>
        [new LlmChatMessage("user", HumanLoopHelper.BuildMissedAskHumanFallbackPrompt(_sendText!))];

    public override IReadOnlyList<JsonObject>? ResolveTools(AgentStepContext context) => null;

    public override LlmConversationOptions BuildOptions(AgentStepContext context) =>
        new(Temperature: context.Config.Temperature, MaxTokens: context.Config.MaxTokens, ResponseSchema: AgentSchemas.MissedAskHumanFallback, ToolChoiceMode: LlmToolChoiceMode.None, AllowParallelToolCalls: false);

    public override ValueTask<bool> ApplyAsync(LlmConversationResponse response, AgentStepContext context, CancellationToken cancellationToken)
    {
        if (!HumanLoopHelper.TryParseMissedAskHumanFallback(response.Content, out var parsed))
        {
            // Not the same fact as "no input needed": the send stays a plain send only because
            // the answer couldn't be read. Say so — this used to fail silently.
            Serilog.Log.Warning("[MissedAskFallback] classifier answer could not be read as JSON — the send is kept as a plain send. Answer preview: {Preview}", response.Content is { Length: > 0 } c ? c[..Math.Min(200, c.Length)] : null);
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(parsed.NeedsHumanInput);
    }
}
