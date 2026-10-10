namespace Agentivity.Infrastructure.Agentic.Agent;

public static class AgentPrompting
{
    public const string StructuredResponseFinalizationPrompt = "Produce the final answer now as valid JSON only, matching the required response schema exactly. Do not call any tools. Do not include markdown fences.";
    public const string ResponseQualityRefinementPromptPrefix = "Quality refinement feedback for the previous answer:";
    public const string SharedToolUsageGuidance = """
<tool_usage_guidance>
- Never simulate tool calls in text, pseudo-code, XML tags, or markdown code blocks. Always use native tool calls.
</tool_usage_guidance>
""";
    public const string SharedExecutionSystemPrompt = SharedToolUsageGuidance;
    public const string DirectAnswerFinalizationPromptPrefix = "Answer the original user request directly now using the full conversation and all collected human input.";

    public static string BuildExecutionPolicySystemPrompt(IReadOnlyList<string> executionPromptRules)
    {
        ArgumentNullException.ThrowIfNull(executionPromptRules);

        var normalizedRules = executionPromptRules.Where(rule => !string.IsNullOrWhiteSpace(rule)).Select(rule => rule.Trim()).ToList();

        if (normalizedRules.Count == 0)
        {
            return string.Empty;
        }

        var rules = string.Join(Environment.NewLine, normalizedRules.Select(rule => $"- {rule}"));
        return $"""
<execution_policy>
{rules}
</execution_policy>
""";
    }

    public static string BuildConversationalResponseFinalizationPrompt(string originalPrompt) =>
        $$"""
<final_response_request>
<platform_instructions>
{{DirectAnswerFinalizationPromptPrefix}}
</platform_instructions>

<original_user_prompt>
<![CDATA[
{{originalPrompt}}
]]>
</original_user_prompt>

<final_response_rules>
- Produce the requested deliverable now.
- Do not say that you can proceed, will proceed later, or are ready to create the answer.
</final_response_rules>

<output_format>
Return the final assistant response only.
</output_format>
</final_response_request>
""";
}
