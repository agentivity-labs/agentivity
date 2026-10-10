using System.Text;
using System.Text.RegularExpressions;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class AgentWorkspaceTextNormalizer
{
    private static readonly Regex MultipleWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    internal static string NormalizeText(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var compacted = MultipleWhitespaceRegex.Replace(content.Trim(), " ");
        return compacted.Length <= 220 ? compacted : $"{compacted[..217]}...";
    }

    internal static string NormalizeKey(string? key)
    {
        var normalized = NormalizeText(key).Replace(':', ' ').Replace('-', ' ').Replace('.', ' ').Trim();

        return MultipleWhitespaceRegex.Replace(normalized.ToLowerInvariant(), "_");
    }

    internal static string NormalizeFingerprint(string? content)
    {
        var compacted = NormalizeText(content);
        if (string.IsNullOrWhiteSpace(compacted))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(compacted.Length);
        var previousWasWhitespace = false;
        foreach (var character in compacted.ToLowerInvariant())
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

    internal static AgentWorkspaceEntry NormalizeEntry(AgentWorkspaceEntry entry) => new(NormalizeKey(entry.Key), NormalizeText(entry.Value));

    internal static void AddUnique(List<AgentWorkspaceEntry> target, AgentWorkspaceEntry candidate, int maxItems)
    {
        if (string.IsNullOrWhiteSpace(candidate.Value) || target.Count >= maxItems)
        {
            return;
        }

        var normalized = NormalizeEntry(candidate);
        if (string.IsNullOrWhiteSpace(normalized.Value))
        {
            return;
        }

        if (target.Any(existing => string.Equals(existing.Key, normalized.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(existing.Value, normalized.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        target.Add(normalized);
    }

    internal static bool HasPotentialSemanticDuplicates(IReadOnlyList<AgentWorkspaceEntry> entries)
    {
        if (entries.Count < 2)
        {
            return false;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var left = NormalizeFingerprint(entries[i].Value);
            if (string.IsNullOrWhiteSpace(left))
            {
                continue;
            }

            for (var j = i + 1; j < entries.Count; j++)
            {
                var right = NormalizeFingerprint(entries[j].Value);
                if (string.IsNullOrWhiteSpace(right))
                {
                    continue;
                }

                if (left.Equals(right, StringComparison.OrdinalIgnoreCase) || left.Contains(right, StringComparison.OrdinalIgnoreCase) || right.Contains(left, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static IEnumerable<AgentWorkspaceEntry> EnumerateEntries(AgentWorkspace knowledgeBase)
    {
        foreach (var entry in knowledgeBase.Goals)
        {
            yield return entry;
        }

        foreach (var entry in knowledgeBase.Facts)
        {
            yield return entry;
        }

        foreach (var entry in knowledgeBase.Constraints)
        {
            yield return entry;
        }

        foreach (var entry in knowledgeBase.Decisions)
        {
            yield return entry;
        }

        foreach (var entry in knowledgeBase.ToolFindings)
        {
            yield return entry;
        }

        foreach (var entry in knowledgeBase.OpenQuestions)
        {
            yield return entry;
        }
    }
}
