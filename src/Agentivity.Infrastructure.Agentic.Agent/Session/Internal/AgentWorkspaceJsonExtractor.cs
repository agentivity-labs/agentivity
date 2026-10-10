using System.Text.Json;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class AgentWorkspaceJsonExtractor
{
    internal static bool TryExtractJsonEntries(LlmChatMessage message, out IReadOnlyList<AgentWorkspaceEntry> entries)
    {
        entries = [];
        if (string.IsNullOrWhiteSpace(message.Content) || !AgentWorkspaceMessageFilter.LooksLikeJsonPayload(message.Content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message.Content);
            if (IsPlanPayload(document.RootElement))
            {
                return true;
            }

            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) && TryExtractStructuredToolFindings(document.RootElement, out var structuredToolFindings))
            {
                entries = structuredToolFindings;
                return true;
            }

            var extracted = new List<AgentWorkspaceEntry>();
            ExtractJsonEntries(document.RootElement, prefix: null, extracted, string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase));
            entries = extracted;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryExtractStructuredToolFindings(JsonElement root, out IReadOnlyList<AgentWorkspaceEntry> entries)
    {
        entries = [];
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
        {
            var findings = new List<AgentWorkspaceEntry>();
            var index = 0;

            foreach (var item in resultsElement.EnumerateArray())
            {
                if (index >= 3 || item.ValueKind != JsonValueKind.Object)
                {
                    break;
                }

                var title = TryReadJsonString(item, "title");
                var snippet = TryReadJsonString(item, "snippet");
                var finding = BuildSearchFinding(title, snippet);
                if (string.IsNullOrWhiteSpace(finding))
                {
                    continue;
                }

                AgentWorkspaceTextNormalizer.AddUnique(findings, new AgentWorkspaceEntry("finding", finding), 8);
                index++;
            }

            entries = findings;
            return true;
        }

        return false;
    }

    internal static void ExtractJsonEntries(JsonElement element, string? prefix, List<AgentWorkspaceEntry> entries, bool toolPayload, int depth = 0)
    {
        if (depth > 3 || entries.Count >= 16)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ShouldIgnoreJsonProperty(property.Name, toolPayload))
                    {
                        continue;
                    }

                    var nextPrefix = string.IsNullOrWhiteSpace(prefix) ? AgentWorkspaceTextNormalizer.NormalizeKey(property.Name) : $"{prefix}.{AgentWorkspaceTextNormalizer.NormalizeKey(property.Name)}";
                    ExtractJsonEntries(property.Value, nextPrefix, entries, toolPayload, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (entries.Count >= 16 || index >= (toolPayload ? 3 : 5))
                    {
                        break;
                    }

                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        var nextPrefix = string.IsNullOrWhiteSpace(prefix) ? $"item_{index + 1}" : $"{prefix}.item_{index + 1}";
                        ExtractJsonEntries(item, nextPrefix, entries, toolPayload, depth + 1);
                    }
                    else if (TryGetScalarValue(item, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        AgentWorkspaceTextNormalizer.AddUnique(entries, new AgentWorkspaceEntry(prefix ?? $"item_{index + 1}", value), 16);
                    }

                    index++;
                }
                break;
            default:
                if (TryGetScalarValue(element, out var scalar) && !string.IsNullOrWhiteSpace(scalar) && !AgentWorkspaceMessageFilter.LooksLikeJsonPayload(scalar))
                {
                    AgentWorkspaceTextNormalizer.AddUnique(entries, new AgentWorkspaceEntry(prefix ?? "value", scalar), 16);
                }
                break;
        }
    }

    internal static bool ShouldIgnoreJsonProperty(string propertyName, bool toolPayload)
    {
        if (toolPayload && propertyName.Equals("results", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (
            toolPayload
            && (
                propertyName.StartsWith("provider", StringComparison.OrdinalIgnoreCase)
                || propertyName.Contains("query", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("rank", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("url", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("snippet", StringComparison.OrdinalIgnoreCase)
                || propertyName.Equals("title", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return true;
        }

        return propertyName.Equals("steps", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("objective", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("description", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("content", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("arguments", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPlanPayload(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("goal", out _) && root.TryGetProperty("steps", out _);
    }

    internal static bool TryGetScalarValue(JsonElement element, out string value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.String => AgentWorkspaceTextNormalizer.NormalizeText(element.GetString()),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => string.Empty,
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    internal static string? TryReadJsonString(JsonElement element, string propertyName) => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? AgentWorkspaceTextNormalizer.NormalizeText(value.GetString()) : null;

    internal static string BuildSearchFinding(string? title, string? snippet)
    {
        var normalizedTitle = AgentWorkspaceTextNormalizer.NormalizeText(title);
        var normalizedSnippet = SummarizeSnippet(snippet);

        return (string.IsNullOrWhiteSpace(normalizedTitle), string.IsNullOrWhiteSpace(normalizedSnippet)) switch
        {
            (false, false) => $"{normalizedTitle}: {normalizedSnippet}",
            (false, true) => normalizedTitle,
            (true, false) => normalizedSnippet,
            _ => string.Empty,
        };
    }

    internal static string SummarizeSnippet(string? snippet)
    {
        var normalized = AgentWorkspaceTextNormalizer.NormalizeText(snippet);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var firstSentence = normalized.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

        return AgentWorkspaceTextNormalizer.NormalizeText(firstSentence ?? normalized);
    }
}
