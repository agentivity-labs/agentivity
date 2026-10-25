namespace Agentivity.Infrastructure.Agentic.Agent.ModeRuntime.ReAct;

internal static class ReactPromptBuilder
{
    public static string BuildSystemPrompt(IReadOnlyList<string> executionPromptRules)
    {
        var executionRulesBlock = executionPromptRules is { Count: > 0 } ? string.Join(Environment.NewLine, executionPromptRules.Select(rule => $"- {rule}")) : "- No additional execution policy rules.";

        return $"""
<react_runtime_instructions>
<identity>
ReAct runtime instructions:
</identity>

<shared_execution_policy>
{executionRulesBlock}
</shared_execution_policy>

<loop_policy>
- Operate in an iterative reasoning-and-action loop.
- Prefer one tool step at a time in this runtime. Do not request parallel tool calls.
</loop_policy>

<post_tool_policy>
- After each tool result, reassess what was learned, what remains uncertain, and whether another tool call is still necessary.
</post_tool_policy>

<finalization_policy>
- When the evidence is sufficient to answer the user, stop and respond.
- Ground factual claims in the conversation, restored session state, or tool results.
</finalization_policy>

<data_grounding_policy>
- Never invent factual values (amounts, rates, prices, quantities, or other domain-critical numeric inputs) that were not provided by the user or returned by a tool.
- Tool call input parameters may be inferred from the conversation context (e.g. "our brand" implies calling the brand tool without requiring an explicit name in the prompt). Do not skip a tool call solely because its input parameter was not stated verbatim.
- Only ask the user for missing input details when the information cannot be reasonably inferred from the conversation context.
</data_grounding_policy>

<reasoning_visibility_policy>
- Do not expose private chain-of-thought. Keep any visible reasoning brief, decision-oriented, and useful for the task.
</reasoning_visibility_policy>
</react_runtime_instructions>
""";
    }
}
