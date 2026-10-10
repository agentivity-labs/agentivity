using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.Steps.Implementations;

/// <summary>
/// The agent's own per-iteration turn — free text or genuine tool calls, using the tool set and
/// parallel-call policy its runtime hands it. The counterpart to
/// <see cref="AgentNextStepDecisionStep"/>: this step never restricts tools on its own and never
/// caps tokens below the agent's own configured budget.
/// </summary>
public sealed class MainTurnStep(IReadOnlyList<JsonObject>? tools, bool allowParallelToolCalls) : AgentStep<LlmConversationResponse>
{
    public override string Name => "main_turn";

    /// <summary>Always runs when asked — it's the first call of an iteration, nothing gates it.</summary>
    public override bool ShouldRun(AgentStepContext context) => true;

    public override IReadOnlyList<JsonObject>? ResolveTools(AgentStepContext context) => tools;

    public override LlmConversationOptions BuildOptions(AgentStepContext context)
    {
        var replySchema = context.Config.RequiresStructuredResponseFinalization
            ? null
            : (string.IsNullOrWhiteSpace(context.Config.ResponseSchema) ? null : context.Config.ResponseSchema);

        return new LlmConversationOptions(Temperature: context.Config.Temperature, MaxTokens: context.Config.MaxTokens, ResponseSchema: replySchema, ToolChoiceMode: context.Config.ToolChoiceMode, AllowParallelToolCalls: allowParallelToolCalls);
    }

    public override ValueTask<LlmConversationResponse> ApplyAsync(LlmConversationResponse response, AgentStepContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(response);
}
