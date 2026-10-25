using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentivity.Module.PromptEngine.Models;

namespace Agentivity.Module.PromptEngine.Prompts;

public static class PromptOptimizationPrompts
{
    private static readonly Regex VariablePlaceholderRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Builds the response schema dynamically based on which components are present on the PromptEntity.
    /// Always includes improved_content. Conditionally includes improved_system_prompt and improved_response_schema.
    /// </summary>
    public static string BuildResponseSchema(PromptEntity PromptEntity)
    {
        var properties = new Dictionary<string, object> { ["improved_content"] = new { type = "string" } };
        var required = new List<string> { "improved_content", "changes_summary", "targeted_dimensions" };

        if (PromptEntity.SystemPrompt is not null)
        {
            properties["improved_system_prompt"] = new { type = "string" };
            required.Add("improved_system_prompt");
        }

        if (PromptEntity.ResponseSchema is not null)
        {
            properties["improved_response_schema"] = new { type = "string" };
            required.Add("improved_response_schema");
        }

        properties["changes_summary"] = new { type = "string", maxLength = 500 };
        properties["targeted_dimensions"] = new
        {
            type = "array",
            items = new { type = "string" },
            maxItems = 5,
        };

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };

        return JsonSerializer.Serialize(schema);
    }

    public static string BuildVariantGenerationPrompt(PromptEntity PromptEntity, string? evaluationCriteria, List<OptimizationIteration> history, string? failureCritique, OptimizationIteration? bestIteration)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            """
            <role>
            You are an expert prompt optimization specialist using evidence-driven iteration.
            You analyze past attempts, identify patterns of success and failure, and generate targeted improvements.
            </role>
            """
        );

        // Describe which components are optimizable
        sb.AppendLine("<optimizable_components>");
        sb.AppendLine("You MUST optimize the following components:");
        sb.AppendLine("- content (the user PromptEntity / main instruction)");
        if (PromptEntity.SystemPrompt is not null)
            sb.AppendLine("- system_prompt (the system message: identity, global rules, persona)");
        if (PromptEntity.ResponseSchema is not null)
            sb.AppendLine("- response_schema (the JSON schema constraining the LLM output format)");
        sb.AppendLine("</optimizable_components>");

        var requiredPlaceholders = GetRequiredPlaceholders(PromptEntity.Content);
        if (requiredPlaceholders.Count > 0)
        {
            sb.AppendLine("<critical_placeholders>");
            sb.AppendLine("You MUST preserve these placeholders exactly as written in the improved user prompt content. Never remove them, rename them, or replace them with concrete values:");
            foreach (var placeholder in requiredPlaceholders)
                sb.AppendLine($"- {placeholder}");
            sb.AppendLine("</critical_placeholders>");
        }

        sb.AppendLine(
            """

            <objective>
            Generate improved versions of ALL the listed components. Your changes must be targeted at the weakest
            dimensions identified in the failure analysis. Do not make random changes — every modification must
            address a specific observed weakness.

            When multiple components are available, think holistically:
            - Move global rules and persona to system_prompt, keep task-specific instructions in content.
            - Tighten response_schema constraints (enums, maxLength, patterns) when format_compliance is weak.
            - Do NOT duplicate instructions across system_prompt and content.
            </objective>

            <optimization_principles>
            1. Fix the weakest dimension first — targeted improvement beats shotgun changes
            2. Preserve what works — high-scoring dimensions should not regress
            3. Be specific — vague PromptEntity instructions produce vague outputs
            4. Add constraints and examples where accuracy or format_compliance are weak
            5. Restructure sections where instruction_following is weak
            6. Add context and edge cases where completeness is weak
            7. Use the right lever: structural constraints belong in response_schema, behavioral rules in system_prompt, task instructions in content
            </optimization_principles>
            """
        );

        sb.AppendLine("<optimization_playbook>");
        sb.AppendLine("Apply these generic PromptEntity-engineering best practices when they are relevant to the current PromptEntity:");
        sb.AppendLine("- Preserve all required placeholders, variables, and critical business constraints.");
        sb.AppendLine("- Keep the user PromptEntity goal explicit, actionable, and specific.");
        sb.AppendLine("- Use the system PromptEntity for stable role, priorities, boundaries, and behavioral rules.");
        sb.AppendLine("- Use the response_schema for structural guarantees, not for business instructions.");
        sb.AppendLine("- Prefer small targeted improvements over full rewrites.");
        sb.AppendLine("- If a section already works well, preserve it and improve only the weak parts.");
        sb.AppendLine("- Reduce ambiguity, but do not remove useful context or constraints.");
        sb.AppendLine("</optimization_playbook>");

        sb.AppendLine("<anti_regression_guardrails>");
        sb.AppendLine("- Never remove or rename critical placeholders.");
        sb.AppendLine("- Never degrade dimensions already scoring strongly unless a change is strictly necessary to fix a critical failure.");
        sb.AppendLine("- Do not rewrite every component if only one or two dimensions are weak.");
        sb.AppendLine("- Preserve the best-known candidate as the baseline and apply only targeted corrections.");
        sb.AppendLine("- Avoid duplicating the same instruction across user PromptEntity and system PromptEntity.");
        if (PromptEntity.SystemPrompt is not null)
            sb.AppendLine("- Keep the system PromptEntity structured: role, priorities, boundaries, and output behavior should remain explicit.");
        if (PromptEntity.ResponseSchema is not null)
            sb.AppendLine("- Keep the response schema valid and strengthen structure only when it helps output reliability.");
        sb.AppendLine("</anti_regression_guardrails>");

        if (evaluationCriteria is not null)
        {
            sb.AppendLine(
                $"""
                <evaluation_criteria>
                {evaluationCriteria}
                </evaluation_criteria>
                """
            );
        }

        if (bestIteration is not null)
        {
            sb.AppendLine("<best_known_candidate>");
            sb.AppendLine($"Use iteration {bestIteration.IterationNumber} as the best-known baseline to preserve.");
            sb.AppendLine($"Best score so far: {bestIteration.Score:F2}");

            if (bestIteration.DimensionScores.Count > 0)
            {
                sb.AppendLine("Strongest dimensions to preserve:");
                foreach (var (dim, score) in bestIteration.DimensionScores.OrderByDescending(d => d.Value).Take(5))
                    sb.AppendLine($"  {dim}: {score:F1}");
            }

            if (bestIteration.StrengthsToPreserve.Count > 0)
            {
                sb.AppendLine("Best candidate strengths to preserve:");
                foreach (var strength in bestIteration.StrengthsToPreserve)
                    sb.AppendLine($"  - {strength}");
            }

            sb.AppendLine("Best candidate components:");
            foreach (var (key, value) in bestIteration.CandidateComponents)
            {
                var truncated = value.Length > 500 ? value[..500] + "... [truncated]" : value;
                sb.AppendLine($"  {key}: {truncated}");
            }

            sb.AppendLine("Generate the next variant by improving from this best-known baseline, not by discarding it.");
            sb.AppendLine("</best_known_candidate>");
        }

        // OPRO-style: pass full history sorted by score ascending (best last = recency bias)
        if (history.Count > 0)
        {
            sb.AppendLine("<optimization_history>");
            sb.AppendLine("Previous attempts sorted by score (worst first, best last).");
            sb.AppendLine("Study the progression to identify what works and what doesn't.");
            sb.AppendLine();

            var sorted = history.OrderBy(h => h.Score).ToList();
            foreach (var iter in sorted)
            {
                sb.AppendLine($"--- Iteration {iter.IterationNumber} | Score: {iter.Score:F2} ---");

                if (iter.DimensionScores.Count > 0)
                {
                    sb.AppendLine("Dimension scores:");
                    foreach (var (dim, score) in iter.DimensionScores.OrderBy(d => d.Value))
                        sb.AppendLine($"  {dim}: {score:F1}");
                }

                if (!string.IsNullOrWhiteSpace(iter.EvaluationSummary))
                    sb.AppendLine($"Summary: {iter.EvaluationSummary}");

                if (iter.StrengthsToPreserve.Count > 0)
                {
                    sb.AppendLine("Strengths to preserve:");
                    foreach (var strength in iter.StrengthsToPreserve)
                        sb.AppendLine($"  - {strength}");
                }

                if (iter.WeaknessesToFix.Count > 0)
                {
                    sb.AppendLine("Weaknesses to fix:");
                    foreach (var weakness in iter.WeaknessesToFix)
                        sb.AppendLine($"  - {weakness}");
                }

                if (iter.NextIterationInstructions.Count > 0)
                {
                    sb.AppendLine("Next iteration instructions:");
                    foreach (var instruction in iter.NextIterationInstructions)
                        sb.AppendLine($"  - {instruction}");
                }

                // Show components (truncated)
                foreach (var (key, value) in iter.CandidateComponents)
                {
                    var truncated = value.Length > 400 ? value[..400] + "... [truncated]" : value;
                    sb.AppendLine($"  {key}: {truncated}");
                }

                sb.AppendLine();
            }

            sb.AppendLine("</optimization_history>");
        }

        // ProTeGi-style: explicit failure critique from last iteration
        if (failureCritique is not null)
        {
            sb.AppendLine(
                $"""
                <failure_critique>
                This is a detailed analysis of why the most recent attempt failed.
                Your improved PromptEntity MUST address these specific failures:

                {failureCritique}
                </failure_critique>
                """
            );
        }

        if (history.Count > 0)
        {
            var latest = history[^1];
            sb.AppendLine("<latest_iteration_guardrails>");
            sb.AppendLine($"Most recent iteration: {latest.IterationNumber} with score {latest.Score:F2}");

            if (latest.WeaknessesToFix.Count > 0)
            {
                sb.AppendLine("Fix these weaknesses from the most recent iteration:");
                foreach (var weakness in latest.WeaknessesToFix.Take(5))
                    sb.AppendLine($"  - {weakness}");
            }

            if (latest.NextIterationInstructions.Count > 0)
            {
                sb.AppendLine("Follow these next-iteration instructions:");
                foreach (var instruction in latest.NextIterationInstructions.Take(5))
                    sb.AppendLine($"  - {instruction}");
            }

            sb.AppendLine("Do not regress the strong dimensions already demonstrated by the best-known candidate.");
            sb.AppendLine("</latest_iteration_guardrails>");
        }

        // Show all current components
        sb.AppendLine("<current_components>");
        sb.AppendLine($"<content>\n{PromptEntity.Content}\n</content>");
        if (PromptEntity.SystemPrompt is not null)
            sb.AppendLine($"<system_prompt>\n{PromptEntity.SystemPrompt}\n</system_prompt>");
        if (PromptEntity.ResponseSchema is not null)
            sb.AppendLine($"<response_schema>\n{PromptEntity.ResponseSchema}\n</response_schema>");
        sb.AppendLine("</current_components>");

        sb.AppendLine();
        sb.AppendLine("Return valid JSON only. The \"targeted_dimensions\" field must list which evaluation dimensions your changes aim to improve.");

        return sb.ToString();
    }

    internal static IReadOnlyList<string> GetRequiredPlaceholders(string content) => VariablePlaceholderRegex.Matches(content).Select(match => match.Value).Distinct(StringComparer.Ordinal).ToList();
}
