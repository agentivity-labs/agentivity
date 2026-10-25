using System.Text;
using Agentivity.Infrastructure.Workflows.Interactions;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class HumanLoopClarificationResolver
{
    internal static List<string> ResolveHumanLoopClarificationKeys(string argumentsJson, string? question, IReadOnlyList<string> questionsToAsk, IReadOnlyList<InteractionField> fields)
    {
        var explicitKeys = HumanLoopToolArgumentExtractor.GetToolArgumentStringArray(argumentsJson, "clarification_keys").Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (explicitKeys.Count > 0)
        {
            return explicitKeys;
        }

        var inferredKeys = new List<string>();

        foreach (var field in fields)
        {
            TryAddClarificationKey(inferredKeys, field.Name);
            TryAddClarificationKey(inferredKeys, field.Label);
        }

        TryAddClarificationKey(inferredKeys, question);
        foreach (var questionToAsk in questionsToAsk)
        {
            TryAddClarificationKey(inferredKeys, questionToAsk);
        }

        return inferredKeys.Count > 0 ? inferredKeys : [NormalizeClarificationKey(question ?? questionsToAsk.FirstOrDefault() ?? "clarification")];
    }

    internal static void TryAddClarificationKey(List<string> keys, string? candidate)
    {
        var key = TryInferClarificationKey(candidate);
        if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(key);
        }
    }

    internal static string NormalizeClarificationKey(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasSeparator = false;

        foreach (var character in NormalizeClarificationSemanticText(text))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    internal static string BuildResolvedClarificationStateBlock(IReadOnlyList<HumanLoopClarificationState> clarifications)
    {
        var activeClarifications = clarifications.Where(clarification => clarification.IsActive).OrderBy(clarification => clarification.LastUpdatedAtTurn).ThenBy(clarification => clarification.ClarificationKey, StringComparer.OrdinalIgnoreCase).TakeLast(12).ToList();

        if (activeClarifications.Count == 0)
        {
            return "Resolved clarification state:\n- None recorded.\n";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Resolved clarification state:");
        builder.AppendLine("These clarifications have already been answered by the human. Treat them as resolved unless contradicted later or unless the immediate next step truly requires stricter precision.");
        foreach (var clarification in activeClarifications)
        {
            builder.AppendLine($"- {clarification.ClarificationKey}:");
            builder.AppendLine($"  - status: {clarification.ResolutionStatus}");
            builder.AppendLine($"  - summary: {clarification.NormalizedSummary}");
            builder.AppendLine($"  - re-ask policy: {DescribeReAskPolicy(clarification.ReAskPolicy)}");
        }

        return builder.ToString().TrimEnd();
    }

    internal static string DescribeReAskPolicy(HumanLoopClarificationReAskPolicy policy) =>
        policy switch
        {
            HumanLoopClarificationReAskPolicy.BlockReAsk => "do not re-ask",
            HumanLoopClarificationReAskPolicy.AllowIfContradicted => "only re-ask if later information contradicts this answer",
            HumanLoopClarificationReAskPolicy.AllowIfToolRequiresExact => "only re-ask if the immediate next step truly requires stricter precision",
            HumanLoopClarificationReAskPolicy.AllowIfExpired => "re-ask only if this information has expired",
            _ => "re-asking is allowed",
        };

    private static string? TryInferClarificationKey(string? candidate)
    {
        var normalized = NormalizeClarificationSemanticText(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (ContainsAny(normalized, "website", "site url", "site_url", "website address", "website_address", "site web", "url du site"))
        {
            return "website_url";
        }

        if (ContainsAny(normalized, "audience", "target audience", "market segment", "segment", "market"))
        {
            return "target_audience";
        }

        if (ContainsAny(normalized, "budget", "available budget"))
        {
            return "budget_limit";
        }

        if (ContainsAny(normalized, "departure", "return", "trip dates", "travel dates", "depart", "retour", "date exacte", "dates exactes"))
        {
            return "trip_dates";
        }

        if (ContainsAny(normalized, "duration", "days", "stay", "jours"))
        {
            return "trip_duration";
        }

        if (ContainsAny(normalized, "traveler", "traveller", "passenger", "people", "person"))
        {
            return "traveler_count";
        }

        return null;
    }

    private static string NormalizeClarificationSemanticText(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Replace('_', ' ').Trim().ToLowerInvariant();

    private static bool ContainsAny(string value, params string[] candidates) => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
