using System.Globalization;
using System.Text.Json;

namespace Agentivity.Infrastructure.Workflows.Conditions;

/// <summary>
/// Shared loose-typing helpers for evaluating a resolved (already <c>{{ }}</c>-substituted) value
/// as a boolean, a number, or a string — used by both <c>CoreIfNode</c>'s legacy scalar
/// <c>condition</c> field and <see cref="ConditionEvaluator"/>'s per-row operators, so the two
/// don't drift into slightly different truthiness rules.
/// </summary>
public static class ConditionValueCoercion
{
    /// <summary>
    /// Same truthiness rules <c>CoreIfNode</c> has always used: null/false-like values are false,
    /// non-empty strings that aren't a recognized false spelling are true.
    /// </summary>
    public static bool ToBoolean(object? value)
    {
        if (value is null)
            return false;

        if (value is bool b)
            return b;

        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => je.GetDouble() != 0,
                JsonValueKind.String => ParseStringAsBoolean(je.GetString()),
                JsonValueKind.Null or JsonValueKind.Undefined => false,
                JsonValueKind.Array => je.GetArrayLength() > 0,
                JsonValueKind.Object => true,
                _ => true,
            };
        }

        if (value is string s)
            return ParseStringAsBoolean(s);

        return value switch
        {
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            decimal dec => dec != 0,
            _ => true,
        };
    }

    /// <summary>Tries to read <paramref name="value"/> as a number, for the ordering operators.</summary>
    public static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case decimal dec:
                result = (double)dec;
                return true;
            case bool:
                result = 0;
                return false;
            case JsonElement { ValueKind: JsonValueKind.Number } je:
                result = je.GetDouble();
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } je:
                return double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>Renders any resolved value as text, for string-based operators (contains, starts with, ...).</summary>
    public static string ToText(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            double d => d.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            decimal dec => dec.ToString(CultureInfo.InvariantCulture),
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.String => je.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => je.GetRawText(),
            },
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>True when the resolved value is "nothing" — null, empty/whitespace string, or an empty array/object.</summary>
    public static bool IsEmpty(object? value)
    {
        return value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            JsonElement je => je.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => true,
                JsonValueKind.String => string.IsNullOrWhiteSpace(je.GetString()),
                JsonValueKind.Array => je.GetArrayLength() == 0,
                JsonValueKind.Object => !je.EnumerateObject().Any(),
                _ => false,
            },
            System.Collections.ICollection collection => collection.Count == 0,
            _ => false,
        };
    }

    private static bool ParseStringAsBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim().ToLowerInvariant();

        if (trimmed is "true" or "1" or "yes" or "y")
            return true;

        if (trimmed is "false" or "0" or "no" or "n")
            return false;

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number != 0;

        return true;
    }
}
