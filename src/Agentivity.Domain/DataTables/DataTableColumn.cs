using System.Globalization;
using System.Text.Json.Serialization;
using Agentivity.Domain.Arguments;

namespace Agentivity.Domain.DataTables;

/// <summary>
/// Defines a column in a DataTable: its JSON definition shape, type constraints, and validation behavior.
/// </summary>
public sealed class DataTableColumn
{
    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("type")]
    public string TypeName { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("default")]
    public object? Default { get; init; }

    [JsonIgnore]
    public ArgumentType Type => Enum.TryParse<ArgumentType>(TypeName, ignoreCase: true, out var t) ? t : ArgumentType.Text;

    [JsonIgnore]
    public object? DefaultValue => Default;

    public DataTableColumn()
    {
        Name = string.Empty;
        TypeName = "Text";
    }

    public DataTableColumn(string name, ArgumentType type, bool required = false, object? defaultValue = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Column name cannot be null or whitespace.", nameof(name));

        Name = name;
        TypeName = type.ToString();
        Required = required;
        Default = defaultValue;
    }

    /// <summary>
    /// Validates that a value is compatible with this column's type.
    /// </summary>
    public bool IsValidValue(object? value)
    {
        if (value is null)
            return !Required || Default is not null;

        return Type switch
        {
            ArgumentType.Text => value is string || true,
            ArgumentType.Number => IsNumeric(value),
            ArgumentType.Boolean => value is bool || (value is string s && bool.TryParse(s, out _)),
            ArgumentType.TextList => value is System.Collections.IEnumerable,
            _ => true,
        };
    }

    /// <summary>
    /// Coerces a value to the appropriate type for this column.
    /// </summary>
    public object? CoerceValue(object? value)
    {
        if (value is null)
            return Default;

        return Type switch
        {
            ArgumentType.Text => value.ToString(),
            ArgumentType.Number => ConvertToNumber(value),
            ArgumentType.Boolean => ConvertToBoolean(value),
            ArgumentType.TextList => ConvertToTextList(value),
            _ => value,
        };
    }

    private static bool IsNumeric(object value) => value is int or long or double or float or decimal || (value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

    private static double? ConvertToNumber(object? value) =>
        value switch
        {
            null => null,
            int i => i,
            long l => l,
            double d => d,
            float f => f,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };

    private static bool? ConvertToBoolean(object? value) =>
        value switch
        {
            null => null,
            bool b => b,
            string s when bool.TryParse(s, out var p) => p,
            _ => null,
        };

    private static List<string>? ConvertToTextList(object? value) =>
        value switch
        {
            null => null,
            IEnumerable<string> strings => strings.ToList(),
            IEnumerable<object?> objects => objects.Select(o => o?.ToString() ?? string.Empty).ToList(),
            string single => [single],
            _ => null,
        };
}
