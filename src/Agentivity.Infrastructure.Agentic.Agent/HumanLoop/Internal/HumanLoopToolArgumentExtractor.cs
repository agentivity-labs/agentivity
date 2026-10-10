using System.Text.Json;

namespace Agentivity.Infrastructure.Agentic.Agent;

internal static class HumanLoopToolArgumentExtractor
{
    internal static string? GetToolArgumentString(string argumentsJson, string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return property.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static List<string> GetToolArgumentStringArray(string argumentsJson, string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string? GetToolArgumentJson(string argumentsJson, string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
