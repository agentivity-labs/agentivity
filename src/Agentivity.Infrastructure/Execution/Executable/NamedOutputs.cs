using System.Text.Json;
using Agentivity.Domain.Executable;

namespace Agentivity.Infrastructure.Execution.Executable;

/// <summary>
/// The named outputs of an agent, a team or a workflow: what it gives to whoever called it next to its answer, each under its
/// own name (a step of a workflow reads one as <c>{{step.name}}</c>). The answer itself is always the output "output"; a named
/// output never takes that name.
/// </summary>
public static class NamedOutputs
{
    /// <summary>The name of the answer, which is always there and is not a named output.</summary>
    public const string Answer = "output";

    /// <summary>
    /// The named outputs a format of answer declares (a JSON Schema, as an agent's <c>response_schema</c>): one per field of the
    /// object it describes, typed as the field is. A format that describes no object declares none.
    /// </summary>
    public static IReadOnlyList<PortDefinition> FromSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return [];
        try
        {
            using var document = JsonDocument.Parse(schema);
            return FromSchema(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <inheritdoc cref="FromSchema(string?)"/>
    public static IReadOnlyList<PortDefinition> FromSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.String)
            return FromSchema(schema.GetString());
        if (schema.ValueKind != JsonValueKind.Object)
            return [];

        // A format given the way some models take it ({ "name": …, "schema": { … } }) describes its object under "schema".
        if (!schema.TryGetProperty("properties", out var properties) && schema.TryGetProperty("schema", out var inner) && inner.ValueKind == JsonValueKind.Object)
            return FromSchema(inner);
        if (properties.ValueKind != JsonValueKind.Object)
            return [];

        return
        [
            .. properties
                .EnumerateObject()
                .Where(field => !string.Equals(field.Name, Answer, StringComparison.OrdinalIgnoreCase))
                .Select(field => new PortDefinition(field.Name, TypeOf(field.Value), DescriptionOf(field.Value))),
        ];
    }

    /// <summary>
    /// The values of the <paramref name="declared"/> named outputs, read from an answer given as a JSON object: each one the field
    /// of the same name. An answer that is not a JSON object gives none; a field that is not in the answer is not given.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Read(IReadOnlyList<PortDefinition> declared, string? answer)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var names = declared.Where(port => !string.Equals(port.Name, Answer, StringComparison.OrdinalIgnoreCase)).Select(port => port.Name).ToList();
        if (names.Count == 0 || ParseObject(answer) is not { } fields)
            return values;

        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var field))
                values[name] = ValueOf(field);
        }
        return values;
    }

    /// <summary>
    /// A value as an output holds it: text, a whole number, a number, yes or no, a list of texts; anything else (an object, a list
    /// of objects) as its JSON text, which a step reads further with <c>{{step.name.field}}</c>.
    /// </summary>
    public static object? ValueOf(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var whole) ? (object)whole : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) => (IReadOnlyList<string>)[.. value.EnumerateArray().Select(item => item.GetString()!)],
            _ => value.GetRawText(),
        };

    /// <summary>
    /// An answer with its named outputs, as a text for someone to read: the answer, then the outputs it does not already hold
    /// (an answer given as a JSON object holds its own fields), each under its name.
    /// </summary>
    public static string Describe(string? answer, IReadOnlyDictionary<string, object?> outputs)
    {
        var text = answer ?? string.Empty;
        var held = ParseObject(text);
        var missing = outputs.Where(o => held is null || !held.ContainsKey(o.Key)).ToList();
        if (missing.Count == 0)
            return text;

        var lines = string.Join("\n", missing.Select(o => $"- {o.Key}: {TextOf(o.Value)}"));
        return string.IsNullOrWhiteSpace(text) ? $"Outputs:\n{lines}" : $"{text}\n\nOutputs:\n{lines}";
    }

    /// <summary>A value of an output as a text: a list as its items, a JSON value as itself.</summary>
    public static string TextOf(object? value) =>
        Normalize(value) switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IReadOnlyList<string> list => string.Join(", ", list),
            var other => other.ToString() ?? string.Empty,
        };

    /// <summary>
    /// A value as an output holds it, whatever it came as: one read back from a stored run comes as a JSON value
    /// (see <see cref="ValueOf"/>).
    /// </summary>
    public static object? Normalize(object? value) => value is JsonElement element ? ValueOf(element) : value;

    private static Dictionary<string, JsonElement>? ParseObject(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;

        // A model may still wrap its JSON in a block of code.
        var text = answer.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start)
                text = text[(start + 1)..end].Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return document.RootElement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TypeOf(JsonElement field)
    {
        var type = field.ValueKind == JsonValueKind.Object && field.TryGetProperty("type", out var t) ? t : default;
        // A type given as a list (["string", "null"]): the first that is not "null".
        var name = type.ValueKind switch
        {
            JsonValueKind.String => type.GetString(),
            JsonValueKind.Array => type.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).FirstOrDefault(item => item != "null"),
            _ => null,
        };
        return name switch
        {
            "string" => "text",
            "integer" or "number" => "number",
            "boolean" => "boolean",
            "array" => "list",
            "object" => "json",
            _ => "value",
        };
    }

    private static string? DescriptionOf(JsonElement field) =>
        field.ValueKind == JsonValueKind.Object && field.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : null;
}
