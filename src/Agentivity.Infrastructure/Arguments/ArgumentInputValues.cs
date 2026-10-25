using Agentivity.Domain.Arguments;

namespace Agentivity.Infrastructure.Arguments;

/// <summary>
/// Provides typed access to resolved node input values.
/// Each supported type has its own getter — no generic Get&lt;T&gt;.
/// This prevents silent type mismatches (e.g. reading a JSON string as JsonElement).
///
/// Values come from SmartJsonStringConverterFactory which converts JSON to native types:
/// - string → string
/// - JSON object → Dictionary&lt;string, object?&gt;
/// - JSON array → List&lt;object?&gt;
/// - number → int/long/double
/// - boolean → bool
/// </summary>
public sealed class ArgumentInputValues
{
    private readonly Dictionary<string, ArgumentDefinition> _valuesByName;

    public ArgumentInputValues(IEnumerable<ArgumentDefinition> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _valuesByName = new Dictionary<string, ArgumentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            _valuesByName[value.Name] = value;
        }
    }

    public ArgumentDefinition this[string name] => _valuesByName[name];

    // ──────────────────────────────────────────────
    //  Text
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets a text value. Structured types (Dictionary, List, JsonElement object/array) are serialized to JSON.
    /// </summary>
    public string? GetText(string name)
    {
        var raw = GetRawValue(name);
        if (raw is null)
            return null;

        return raw switch
        {
            string s => s,
            System.Text.Json.JsonElement je => je.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => je.GetString(),
                System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
                _ => je.GetRawText(),
            },
            Dictionary<string, object?> dict => System.Text.Json.JsonSerializer.Serialize(dict),
            List<object?> list => System.Text.Json.JsonSerializer.Serialize(list),
            _ => null,
        };
    }

    /// <summary>
    /// Gets a required text value. Throws if null or whitespace.
    /// </summary>
    public string RequireText(string name)
    {
        var value = GetText(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Missing required text input '{name}'.");
        return value;
    }

    // ──────────────────────────────────────────────
    //  Boolean
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets a boolean value. Returns null if the input is missing or not a boolean.
    /// </summary>
    public bool? GetBoolean(string name)
    {
        var raw = GetRawValue(name);
        if (raw is null)
            return null;

        return raw switch
        {
            bool b => b,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => false,
            _ => null,
        };
    }

    // ──────────────────────────────────────────────
    //  Int
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets an integer value. Handles int, long (if in range), double (if whole), and JsonElement numbers.
    /// </summary>
    public int? GetInt(string name)
    {
        var raw = GetRawValue(name);
        if (raw is null)
            return null;

        return raw switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            double d when d % 1 == 0 && d is >= int.MinValue and <= int.MaxValue => (int)d,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out var i) => i,
            _ => null,
        };
    }

    // ──────────────────────────────────────────────
    //  Long
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets a long value. Handles long, int, double (if whole), and JsonElement numbers.
    /// </summary>
    public long? GetLong(string name)
    {
        var raw = GetRawValue(name);
        if (raw is null)
            return null;

        return raw switch
        {
            long l => l,
            int i => (long)i,
            double d when d % 1 == 0 => (long)d,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt64(out var l) => l,
            _ => null,
        };
    }

    /// <summary>
    /// Gets a required long value. Throws if null.
    /// </summary>
    public long RequireLong(string name)
    {
        return GetLong(name) ?? throw new InvalidOperationException($"Missing required long input '{name}'.");
    }

    // ──────────────────────────────────────────────
    //  Double
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets a double value. Handles double, int, long, and JsonElement numbers.
    /// </summary>
    public double? GetDouble(string name)
    {
        var raw = GetRawValue(name);
        if (raw is null)
            return null;

        return raw switch
        {
            double d => d,
            int i => (double)i,
            long l => (double)l,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            _ => null,
        };
    }

    // ──────────────────────────────────────────────
    //  JsonElement
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets the value as a JsonElement. Converts from other types if needed.
    /// Handles: native JsonElement, Dictionary, List, and JSON strings starting with { or [.
    /// </summary>
    public System.Text.Json.JsonElement? GetJsonElement(string name)
    {
        var raw = GetRawValue(name);
        if (raw is null)
            return null;

        return raw switch
        {
            System.Text.Json.JsonElement je => je,
            Dictionary<string, object?> dict => System.Text.Json.JsonSerializer.SerializeToElement(dict),
            List<object?> list => System.Text.Json.JsonSerializer.SerializeToElement(list),
            string json when json.TrimStart().StartsWith('{') || json.TrimStart().StartsWith('[') => System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(),
            _ => null,
        };
    }

    // ──────────────────────────────────────────────
    //  Json (structured object)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets the value as a JSON structure (Dictionary, List, or JsonElement).
    /// Returns null if it's a plain string or primitive type.
    /// </summary>
    public object? GetJson(string name)
    {
        var raw = GetRawValue(name);
        if (raw is null)
            return null;

        return raw switch
        {
            System.Text.Json.JsonElement je => je,
            Dictionary<string, object?> dict => dict,
            IDictionary<string, object?> idict => new Dictionary<string, object?>(idict, StringComparer.OrdinalIgnoreCase),
            List<object?> list => list,
            IList<object?> ilist => ilist.ToList(),
            _ => null,
        };
    }

    // ──────────────────────────────────────────────
    //  String List
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets a string list from various sources (native list, List&lt;object?&gt;, JsonElement array, or single string).
    /// </summary>
    public IReadOnlyList<string> GetStringList(string name, IReadOnlyList<string> fallback)
    {
        var raw = GetRawValue(name);
        if (raw is null)
            return fallback;

        var result = ConvertToStringList(raw);
        return result.Count > 0 ? result : fallback;
    }

    /// <summary>
    /// Gets a required string list. Throws if empty.
    /// </summary>
    public IReadOnlyList<string> RequireStringList(string name)
    {
        var list = GetStringList(name, []);
        if (list.Count == 0)
            throw new InvalidOperationException($"Missing required string list input '{name}'.");
        return list;
    }

    // ──────────────────────────────────────────────
    //  Raw (untyped)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Gets the raw value without type conversion.
    /// Use only when the consumer handles multiple types dynamically (e.g. datatable data input).
    /// </summary>
    public object? GetRaw(string name)
    {
        return GetRawValue(name);
    }

    // ──────────────────────────────────────────────
    //  Internal
    // ──────────────────────────────────────────────

    private object? GetRawValue(string name)
    {
        // Absent from _valuesByName means this node genuinely never declared the input (e.g.
        // AiNode has no "max_tokens" input at all — see AgentRuntimeParameterResolver, shared with
        // the standalone AgentEntityRunner path, which still queries it) — indistinguishable in
        // practice from "declared but not provided, no default", so treated the same way (null)
        // rather than throwing. A genuinely misspelled input name is caught by the node's own
        // characterization tests, not by a runtime exception here.
        return _valuesByName.TryGetValue(name, out var value) ? value.Value ?? value.DefaultValue : null;
    }

    private static IReadOnlyList<string> ConvertToStringList(object raw)
    {
        if (raw is string s)
            return string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()];

        if (raw is IReadOnlyList<string> readOnlyList)
            return readOnlyList;

        if (raw is List<string> stringList)
            return stringList;

        if (raw is string[] stringArray)
            return stringArray;

        if (raw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            return je.EnumerateArray().Select(e => e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : e.GetRawText()).Where(str => !string.IsNullOrWhiteSpace(str)).Select(str => str!.Trim()).ToList();
        }

        if (raw is List<object?> objectList)
        {
            return objectList.Select(x => x?.ToString()).Where(str => !string.IsNullOrWhiteSpace(str)).Select(str => str!.Trim()).ToList();
        }

        if (raw is IEnumerable<object?> enumerable)
        {
            return enumerable.Select(x => x?.ToString()).Where(str => !string.IsNullOrWhiteSpace(str)).Select(str => str!.Trim()).ToList();
        }

        return [];
    }
}
