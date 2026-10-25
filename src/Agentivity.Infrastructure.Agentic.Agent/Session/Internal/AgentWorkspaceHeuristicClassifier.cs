namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class AgentWorkspaceHeuristicClassifier
{
    internal static bool IsQuestion(string fragment) =>
        fragment.Contains('?', StringComparison.Ordinal)
        || fragment.StartsWith("who ", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("what ", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("when ", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("where ", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("how ", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("quel", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("quelle", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("quels", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("quelles", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("combien", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("voulez-vous", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("préférez-vous", StringComparison.OrdinalIgnoreCase)
        || fragment.StartsWith("preferez-vous", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeGoal(string fragment) =>
        fragment.Contains("want", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("need", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("looking for", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("prepare", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("create", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("build", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("plan", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("estimate", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("je veux", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("j'ai besoin", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("prépare", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("crée", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("cree", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeConstraint(string fragment) =>
        fragment.Any(char.IsDigit)
        || fragment.Contains("€", StringComparison.Ordinal)
        || fragment.Contains("$", StringComparison.Ordinal)
        || fragment.Contains("%", StringComparison.Ordinal)
        || fragment.Contains("budget", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("max", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("minimum", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("per person", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("for 2", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("for two", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("people", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("personnes", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("aller-retour", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("round trip", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("return", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("departure", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("from ", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("to ", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("depuis", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("vers", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("nuit", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("night", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeDecisionOrPreference(string fragment) =>
        fragment.Contains("simplement", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("avec un lien", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("sans réserver", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("sans reserver", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("ne réserve", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("ne reserve", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("proposition", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("proposer", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("propose", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("préfère", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("prefere", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("plutôt", StringComparison.OrdinalIgnoreCase)
        || fragment.Contains("plutot", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fragment, "vas y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fragment, "go ahead", StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeDecisionQuestion(string question) =>
        question.Contains("préf", StringComparison.OrdinalIgnoreCase)
        || question.Contains("pref", StringComparison.OrdinalIgnoreCase)
        || question.Contains("voulez-vous", StringComparison.OrdinalIgnoreCase)
        || question.Contains("souhaitez-vous", StringComparison.OrdinalIgnoreCase)
        || question.Contains("réserve", StringComparison.OrdinalIgnoreCase)
        || question.Contains("reserve", StringComparison.OrdinalIgnoreCase);

    internal static string BuildEntryKey(string value)
    {
        if (LooksLikeGoal(value))
        {
            return "goal";
        }

        if (LooksLikeDecisionOrPreference(value))
        {
            return "preference";
        }

        if (LooksLikeConstraint(value))
        {
            return "constraint";
        }

        return "fact";
    }
}
