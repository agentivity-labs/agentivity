using System.Text.RegularExpressions;
using Agentivity.Module.PromptEngine.Models;

namespace Agentivity.Module.PromptEngine.Services;

internal static class PromptPlaceholderGuardrails
{
    private static readonly Regex VariablePlaceholderRegex = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static IReadOnlyList<string> GetRequiredPlaceholders(PromptEntity PromptEntity)
    {
        ArgumentNullException.ThrowIfNull(PromptEntity);

        var declaredVariables = PromptEntity.Variables.Select(variable => variable.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => $"{{{{{name}}}}}");

        var discoveredPlaceholders = GetPlaceholders(PromptEntity.Content).Concat(GetPlaceholders(PromptEntity.SystemPrompt));

        return declaredVariables.Concat(discoveredPlaceholders).Distinct(StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> GetMissingPlaceholders(PromptEntity PromptEntity, string? candidateUserPrompt, string? candidateSystemPrompt = null)
    {
        var requiredPlaceholders = GetRequiredPlaceholders(PromptEntity);
        if (requiredPlaceholders.Count == 0)
            return [];

        var candidateSurface = $"{candidateUserPrompt ?? string.Empty}\n{candidateSystemPrompt ?? string.Empty}";
        return requiredPlaceholders.Where(placeholder => !candidateSurface.Contains(placeholder, StringComparison.Ordinal)).ToList();
    }

    public static string BuildPreservationInstruction(IEnumerable<string> placeholders) => $"Preserve critical placeholders exactly: {string.Join(", ", placeholders)}.";

    public static string MergeWithSummary(string? summary, IEnumerable<string> placeholders)
    {
        var preservationInstruction = BuildPreservationInstruction(placeholders);
        if (string.IsNullOrWhiteSpace(summary))
            return preservationInstruction;

        return summary.Contains(preservationInstruction, StringComparison.OrdinalIgnoreCase) ? summary : $"{summary} {preservationInstruction}";
    }

    public static string PreservePlaceholdersInUserPrompt(PromptEntity PromptEntity, string? candidateUserPrompt, string? candidateSystemPrompt = null)
    {
        var adjustedUserPrompt = candidateUserPrompt ?? PromptEntity.Content;
        var missingPlaceholders = GetMissingPlaceholders(PromptEntity, adjustedUserPrompt, candidateSystemPrompt);
        if (missingPlaceholders.Count == 0)
            return adjustedUserPrompt;

        var originalContentPlaceholders = GetPlaceholders(PromptEntity.Content).ToHashSet(StringComparer.Ordinal);

        if (missingPlaceholders.Any(originalContentPlaceholders.Contains))
        {
            adjustedUserPrompt = PromptEntity.Content;
            missingPlaceholders = GetMissingPlaceholders(PromptEntity, adjustedUserPrompt, candidateSystemPrompt);
        }

        return missingPlaceholders.Count == 0 ? adjustedUserPrompt : AppendMissingPlaceholders(adjustedUserPrompt, missingPlaceholders);
    }

    public static List<PromptIssue> BuildMissingPlaceholderIssues(IEnumerable<string> placeholders) =>
        placeholders
            .Select(placeholder => new PromptIssue
            {
                Category = "placeholders",
                Severity = "high",
                Description = $"The improved user PromptEntity removed the critical placeholder {placeholder}.",
                Suggestion = $"Preserve {placeholder} exactly in the improved user PromptEntity so required inputs remain available.",
            })
            .ToList();

    private static IEnumerable<string> GetPlaceholders(string? content) => string.IsNullOrWhiteSpace(content) ? [] : VariablePlaceholderRegex.Matches(content).Select(match => match.Value).Distinct(StringComparer.Ordinal);

    private static string AppendMissingPlaceholders(string userPrompt, IReadOnlyList<string> missingPlaceholders)
    {
        var suffix = $"Required input placeholders to preserve: {string.Join(", ", missingPlaceholders)}.";
        if (string.IsNullOrWhiteSpace(userPrompt))
            return suffix;

        return userPrompt.Contains(suffix, StringComparison.OrdinalIgnoreCase) ? userPrompt : $"{userPrompt}\n\n{suffix}";
    }
}
