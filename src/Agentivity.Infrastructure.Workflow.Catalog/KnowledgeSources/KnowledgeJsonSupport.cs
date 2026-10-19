using System.Text.Json;

namespace Agentivity.Infrastructure.Workflow.Catalog.KnowledgeSources;

internal static class KnowledgeJsonSupport
{
    public static JsonElement SelectElement(JsonElement element, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return element;
        }

        JsonElement current = element;
        foreach (string rawSegment in selector.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string segment = rawSegment;
            while (segment.Length > 0)
            {
                int bracketIndex = segment.IndexOf('[');
                if (bracketIndex < 0)
                {
                    current = SelectProperty(current, segment);
                    segment = string.Empty;
                    continue;
                }

                if (bracketIndex > 0)
                {
                    current = SelectProperty(current, segment[..bracketIndex]);
                }

                int closingIndex = segment.IndexOf(']', bracketIndex + 1);
                if (closingIndex < 0)
                {
                    throw new InvalidOperationException($"Invalid selector segment '{rawSegment}'.");
                }

                string indexText = segment.Substring(bracketIndex + 1, closingIndex - bracketIndex - 1);
                if (!int.TryParse(indexText, out int itemIndex))
                {
                    throw new InvalidOperationException($"Invalid selector index '{indexText}' in segment '{rawSegment}'.");
                }

                if (current.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"Selector segment '{rawSegment}' expected an array.");
                }

                if (itemIndex < 0 || itemIndex >= current.GetArrayLength())
                {
                    throw new InvalidOperationException($"Selector index '{itemIndex}' is out of range for segment '{rawSegment}'.");
                }

                current = current[itemIndex];
                segment = closingIndex + 1 >= segment.Length ? string.Empty : segment[(closingIndex + 1)..];
            }
        }

        return current;
    }

    public static string ElementToText(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => element.ToString(),
        };

    private static JsonElement SelectProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out JsonElement property) ? throw new InvalidOperationException($"JSON selector property '{propertyName}' was not found.") : property;
    }
}
