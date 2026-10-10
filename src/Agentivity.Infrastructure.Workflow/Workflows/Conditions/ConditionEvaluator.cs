namespace Agentivity.Infrastructure.Workflows.Conditions;

/// <summary>
/// Evaluates a <see cref="ConditionGroup"/> against its already-resolved row values. Pure and
/// stateless — no I/O, no workflow-engine dependency — so it's trivially reusable by any node.
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>An empty group (no rows) evaluates to <see langword="false"/> — nothing to match.</summary>
    public static bool Evaluate(ConditionGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (group.Rows.Count == 0)
            return false;

        return group.Combinator switch
        {
            ConditionCombinator.Any => group.Rows.Any(EvaluateRow),
            _ => group.Rows.All(EvaluateRow),
        };
    }

    private static bool EvaluateRow(ConditionRow row)
    {
        return row.Operator switch
        {
            ConditionOperator.IsEmpty => ConditionValueCoercion.IsEmpty(row.Field),
            ConditionOperator.IsNotEmpty => !ConditionValueCoercion.IsEmpty(row.Field),
            ConditionOperator.IsTrue => ConditionValueCoercion.ToBoolean(row.Field),
            ConditionOperator.IsFalse => !ConditionValueCoercion.ToBoolean(row.Field),
            ConditionOperator.Contains => ConditionValueCoercion.ToText(row.Field).Contains(row.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotContains => !ConditionValueCoercion.ToText(row.Field).Contains(row.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.StartsWith => ConditionValueCoercion.ToText(row.Field).StartsWith(row.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.EndsWith => ConditionValueCoercion.ToText(row.Field).EndsWith(row.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.GreaterThan => CompareNumeric(row, (a, b) => a > b),
            ConditionOperator.LessThan => CompareNumeric(row, (a, b) => a < b),
            ConditionOperator.GreaterThanOrEqual => CompareNumeric(row, (a, b) => a >= b),
            ConditionOperator.LessThanOrEqual => CompareNumeric(row, (a, b) => a <= b),
            ConditionOperator.NotEquals => !AreEqual(row),
            _ => AreEqual(row),
        };
    }

    private static bool AreEqual(ConditionRow row)
    {
        if (ConditionValueCoercion.TryToDouble(row.Field, out var fieldNumber) && ConditionValueCoercion.TryToDouble(row.Value, out var valueNumber))
            return fieldNumber == valueNumber;

        return string.Equals(ConditionValueCoercion.ToText(row.Field), row.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CompareNumeric(ConditionRow row, Func<double, double, bool> compare)
    {
        if (!ConditionValueCoercion.TryToDouble(row.Field, out var fieldNumber) || !ConditionValueCoercion.TryToDouble(row.Value, out var valueNumber))
            return false;

        return compare(fieldNumber, valueNumber);
    }
}
