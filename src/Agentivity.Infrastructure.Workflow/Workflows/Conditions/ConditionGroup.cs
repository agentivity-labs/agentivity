using System.Text.Json;

namespace Agentivity.Infrastructure.Workflows.Conditions;

/// <summary>
/// Field comparison operators for a <see cref="ConditionRow"/> — deliberately a fixed, small
/// vocabulary (mirrors n8n's basic IF-node operator set) rather than a free-text expression
/// language, so a non-technical workflow author picks one from a dropdown instead of writing code.
/// </summary>
public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    IsEmpty,
    IsNotEmpty,
    IsTrue,
    IsFalse,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
}

/// <summary>How multiple <see cref="ConditionRow"/>s in a <see cref="ConditionGroup"/> combine.</summary>
public enum ConditionCombinator
{
    All,
    Any,
}

/// <summary>
/// One "field &lt;operator&gt; value" clause. <see cref="Field"/> arrives already resolved —
/// the engine substitutes any <c>{{ nodeId.field }}</c> placeholder nested inside the owning
/// node's <c>conditions</c> input before the node ever runs (see <c>ArgumentValueResolver</c>,
/// which recurses into JSON objects/arrays), so this type has no binding/lookup logic of its own.
/// </summary>
public sealed record ConditionRow(object? Field, ConditionOperator Operator, string? Value);

/// <summary>
/// A generic, centralized "field comparisons" primitive — built for <c>core.if</c> but designed to
/// be reused by any future node (a rewritten <c>core.switch</c>, a filter/wait-until node) that
/// wants the same no-code condition builder instead of duplicating operator logic.
/// </summary>
public sealed record ConditionGroup(ConditionCombinator Combinator, IReadOnlyList<ConditionRow> Rows)
{
    public static readonly ConditionGroup Empty = new(ConditionCombinator.All, []);

    /// <summary>
    /// Tolerant parse: a missing/null/malformed <paramref name="raw"/> value (an empty default,
    /// an old workflow that never had this field, an author who cleared it) produces an empty
    /// group rather than throwing — the caller decides what "no conditions" means (typically:
    /// fall back to a legacy path).
    /// </summary>
    public static ConditionGroup Parse(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } obj)
            return Empty;

        var combinator = ConditionCombinator.All;
        if (obj.TryGetProperty("combinator", out var combinatorEl) && combinatorEl.ValueKind == JsonValueKind.String && string.Equals(combinatorEl.GetString(), "any", StringComparison.OrdinalIgnoreCase))
        {
            combinator = ConditionCombinator.Any;
        }

        if (!obj.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
            return new ConditionGroup(combinator, []);

        var rows = new List<ConditionRow>();
        foreach (var rowEl in rowsEl.EnumerateArray())
        {
            if (rowEl.ValueKind != JsonValueKind.Object)
                continue;

            var op = rowEl.TryGetProperty("operator", out var opEl) && opEl.ValueKind == JsonValueKind.String ? ParseOperator(opEl.GetString()) : ConditionOperator.Equals;
            var field = rowEl.TryGetProperty("field", out var fieldEl) ? ToObject(fieldEl) : null;
            var value = rowEl.TryGetProperty("value", out var valueEl) ? ConditionValueCoercion.ToText(ToObject(valueEl)) : null;

            rows.Add(new ConditionRow(field, op, value));
        }

        return new ConditionGroup(combinator, rows);
    }

    private static ConditionOperator ParseOperator(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "equals" => ConditionOperator.Equals,
            "not_equals" => ConditionOperator.NotEquals,
            "contains" => ConditionOperator.Contains,
            "not_contains" => ConditionOperator.NotContains,
            "starts_with" => ConditionOperator.StartsWith,
            "ends_with" => ConditionOperator.EndsWith,
            "is_empty" => ConditionOperator.IsEmpty,
            "is_not_empty" => ConditionOperator.IsNotEmpty,
            "is_true" => ConditionOperator.IsTrue,
            "is_false" => ConditionOperator.IsFalse,
            "greater_than" => ConditionOperator.GreaterThan,
            "less_than" => ConditionOperator.LessThan,
            "greater_than_or_equal" => ConditionOperator.GreaterThanOrEqual,
            "less_than_or_equal" => ConditionOperator.LessThanOrEqual,
            _ => ConditionOperator.Equals,
        };

    private static object? ToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.Clone(),
        };
}
