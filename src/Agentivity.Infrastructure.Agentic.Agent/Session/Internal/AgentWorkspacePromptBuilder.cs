using System.Text;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class AgentWorkspacePromptBuilder
{
    internal static void AppendSection(StringBuilder builder, string title, IReadOnlyList<AgentWorkspaceEntry> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine(title + ":");
        foreach (var item in items)
        {
            builder.Append("- ").AppendLine(FormatEntry(item));
        }
    }

    internal static string FormatEntry(AgentWorkspaceEntry entry) => string.IsNullOrWhiteSpace(entry.Key) || entry.Key is "fact" or "goal" or "constraint" or "preference" or "question" or "finding" ? entry.Value : $"{entry.Key}: {entry.Value}";

    internal static void AppendAdditionalRules(StringBuilder builder, IReadOnlyList<string>? additionalRules)
    {
        if (additionalRules is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Additional policy rules:");
        foreach (var rule in additionalRules.Where(rule => !string.IsNullOrWhiteSpace(rule)))
        {
            builder.Append("- ").AppendLine(rule.Trim());
        }
    }

    internal static string BuildInlineAdditionalRules(IReadOnlyList<string>? additionalRules) => additionalRules is not { Count: > 0 } ? "(none)" : string.Join(" ", additionalRules.Where(rule => !string.IsNullOrWhiteSpace(rule)).Select(rule => $"- {rule.Trim()}"));
}
