using System.Text.Json;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;

/// <summary>
/// Shared "loosely-typed insert/update payload → row dictionary" conversion, used by every
/// consumer of a Data Table resource (<see cref="CoreDataTableNode"/>'s workflow-step CRUD,
/// <c>AIAgentDataTableToolNode</c>'s LLM-callable CRUD) so the accepted input shapes — a
/// dictionary, a <see cref="JsonElement"/> object, or a JSON object string — stay identical
/// regardless of which node is doing the writing.
/// </summary>
internal static class DataTableRowDataMapping
{
    public static Dictionary<string, object?> ConvertToDictionary(object? value)
    {
        return value switch
        {
            null => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            Dictionary<string, object?> dict => new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase),
            IReadOnlyDictionary<string, object?> readOnly => new Dictionary<string, object?>(readOnly, StringComparer.OrdinalIgnoreCase),
            JsonElement element when element.ValueKind == JsonValueKind.Object => ParseJsonElement(element),
            string json when json.TrimStart().StartsWith('{') => ParseJsonString(json),
            _ => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static Dictionary<string, object?> ParseJsonElement(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            result[property.Name] = ParseJsonValue(property.Value);
        }

        return result;
    }

    private static Dictionary<string, object?> ParseJsonString(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return ParseJsonElement(doc.RootElement);
            }
        }
        catch
        {
            // Invalid JSON
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static object? ParseJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ParseJsonElement(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ParseJsonValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
