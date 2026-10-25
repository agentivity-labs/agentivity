using System.Text;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class HumanLoopResolutionInferrer
{
    internal static HumanLoopClarificationResolutionStatus InferResolutionStatus(string answer)
    {
        var normalizedAnswer = NormalizeQuestionFingerprint(answer);
        if (string.IsNullOrWhiteSpace(normalizedAnswer))
        {
            return HumanLoopClarificationResolutionStatus.Unknown;
        }

        if (ContainsAny(normalizedAnswer, "no preference", "no preferences", "doesnt matter", "does not matter", "dont care", "do not care", "pas de preference", "pas de préférence", "peu importe", "aucune preference", "aucune préférence"))
        {
            return HumanLoopClarificationResolutionStatus.NoPreference;
        }

        if (ContainsAny(normalizedAnswer, "i dont know", "i do not know", "not sure", "no idea", "je ne sais pas", "aucune idee", "ignore"))
        {
            return HumanLoopClarificationResolutionStatus.Declined;
        }

        if (ContainsAny(normalizedAnswer, "around", "about", "approximately", "approx", "early", "début", "late", "tard", "mid", "milieu", "beginning", "minimum", "maximum", "debut", "fin", "environ", "vers", "minimum", "maximum"))
        {
            return HumanLoopClarificationResolutionStatus.Approximate;
        }

        if (ContainsAny(normalizedAnswer, "flexible", "any time", "open to", "no exact", "without exact", "pas de jours precis", "pas de jours précis", "pas de date precise", "pas de date précise", "flexible"))
        {
            return HumanLoopClarificationResolutionStatus.Flexible;
        }

        return HumanLoopClarificationResolutionStatus.Exact;
    }

    internal static HumanLoopClarificationReAskPolicy ResolveReAskPolicy(HumanLoopClarificationResolutionStatus resolutionStatus) =>
        resolutionStatus switch
        {
            HumanLoopClarificationResolutionStatus.Approximate => HumanLoopClarificationReAskPolicy.AllowIfToolRequiresExact,
            HumanLoopClarificationResolutionStatus.Flexible => HumanLoopClarificationReAskPolicy.AllowIfToolRequiresExact,
            HumanLoopClarificationResolutionStatus.Unknown => HumanLoopClarificationReAskPolicy.Allow,
            HumanLoopClarificationResolutionStatus.Contradictory => HumanLoopClarificationReAskPolicy.AllowIfContradicted,
            _ => HumanLoopClarificationReAskPolicy.BlockReAsk,
        };

    internal static bool HasStrictPrecisionJustification(string? questionContext)
    {
        var normalizedContext = NormalizeQuestionFingerprint(questionContext ?? string.Empty);
        return ContainsAny(normalizedContext, "exact", "precise", "booking", "book", "reservation", "reserve", "ticket", "payment", "fare", "availability", "issuance", "exacte", "exact", "precis", "précis", "reservation", "réservation", "billet", "paiement", "disponibilite");
    }

    internal static bool ContainsAny(string value, params string[] candidates) => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeQuestionFingerprint(string question)
    {
        var builder = new StringBuilder(question.Length);
        var previousWasWhitespace = false;

        foreach (var character in question.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasWhitespace = false;
                continue;
            }

            if (!char.IsWhiteSpace(character) || previousWasWhitespace)
            {
                continue;
            }

            builder.Append(' ');
            previousWasWhitespace = true;
        }

        return builder.ToString().Trim();
    }

    internal static string NormalizeContinuationValue(object rawValue) =>
        rawValue switch
        {
            string text => text.Trim(),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } jsonString => jsonString.GetString()?.Trim() ?? string.Empty,
            System.Text.Json.JsonElement jsonElement => jsonElement.ToString(),
            _ => rawValue.ToString()?.Trim() ?? string.Empty,
        };
}
