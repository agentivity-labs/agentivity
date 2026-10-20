using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentivity.Domain.Arguments;

namespace Agentivity.Infrastructure.Arguments;

/// <summary>
/// Resolves <c>{{placeholder}}</c> expressions in argument values against a state dictionary.
/// Supports strings, JSON elements/nodes, dictionaries, and lists — recursively.
/// Used by workflow nodes, agent executables, and any composable that receives a state-based input.
/// </summary>
public static class ArgumentValueResolver
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex PlaceholderRegex = new("\\{\\{\\s*([^\\}\n\\r]+?)\\s*\\}\\}", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PathSegmentRegex = new(@"\.([a-zA-Z_][a-zA-Z0-9_]*)|\[(\d+)\]|\[""([^""]+)""\]|\['([^']+)'\]", RegexOptions.Compiled);

    public static object? Resolve(object? value, IDictionary<string, object?> state)
    {
        if (value is null)
            return null;

        if (value is string text)
            return ResolveString(text, state);

        if (value is JsonElement element)
            return ResolveJsonElement(element, state);

        if (value is JsonNode node)
            return ResolveJsonNode(node, state);

        if (value is IDictionary<string, object?> dict)
            return ResolveDictionary(dict, state);

        if (value is IList<object?> list)
            return ResolveList(list, state);

        return value;
    }

    private static Dictionary<string, object?> ResolveDictionary(IDictionary<string, object?> dict, IDictionary<string, object?> state)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in dict)
            result[key] = Resolve(val, state);
        return result;
    }

    private static List<object?> ResolveList(IList<object?> list, IDictionary<string, object?> state)
    {
        var result = new List<object?>(list.Count);
        foreach (var item in list)
            result.Add(Resolve(item, state));
        return result;
    }

    private static object? ResolveString(string text, IDictionary<string, object?> state)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var trimmed = text.Trim();
        var match = PlaceholderRegex.Match(trimmed);

        // Entire string is a single placeholder: {{ path }}
        if (match.Success && match.Value.Length == trimmed.Length)
        {
            var expression = match.Groups[1].Value.Trim();
            return ResolveExpression(expression, state);
        }

        if (!PlaceholderRegex.IsMatch(text))
            return text;

        if (IsJsonStringWithPlaceholders(trimmed))
            return ResolveJsonString(trimmed, state);

        var interpolated = PlaceholderRegex.Replace(
            text,
            placeholder =>
            {
                var expression = placeholder.Groups[1].Value.Trim();
                var resolved = ResolveExpression(expression, state);
                return ConvertToString(resolved);
            }
        );

        return interpolated;
    }

    private static bool IsJsonStringWithPlaceholders(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2)
            return false;

        var firstChar = text[0];
        var lastChar = text[^1];

        if (!((firstChar == '{' && lastChar == '}') || (firstChar == '[' && lastChar == ']')))
            return false;

        return PlaceholderRegex.IsMatch(text);
    }

    private static object? ResolveJsonString(string jsonText, IDictionary<string, object?> state)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var resolved = ResolveJsonElement(doc.RootElement, state);

            if (resolved is JsonElement je)
                return je.Clone();

            return JsonSerializer.SerializeToElement(resolved);
        }
        catch (JsonException)
        {
            var interpolated = PlaceholderRegex.Replace(
                jsonText,
                placeholder =>
                {
                    var expression = placeholder.Groups[1].Value.Trim();
                    var resolved = ResolveExpression(expression, state);
                    return ConvertToString(resolved);
                }
            );
            return interpolated;
        }
    }

    private static object? ResolveExpression(string expression, IDictionary<string, object?> state)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        expression = expression.Trim();

        if (!TrySplitNodeIdAndPath(expression, state, out var nodeId, out var pathPart))
            return null;

        if (!state.TryGetValue(nodeId, out var nodeOutput))
            return null;

        if (string.IsNullOrWhiteSpace(pathPart))
            return nodeOutput;

        return NavigatePath(nodeOutput, pathPart);
    }

    private static object? NavigatePath(object? root, string pathPart)
    {
        if (root is null || string.IsNullOrWhiteSpace(pathPart))
            return root;

        JsonNode? jsonNode;
        try
        {
            if (root is string str)
                jsonNode = JsonNode.Parse(str);
            else if (root is JsonNode node)
                jsonNode = node;
            else
            {
                var json = JsonSerializer.Serialize(root, SerializerOptions);
                jsonNode = JsonNode.Parse(json);
            }
        }
        catch
        {
            return root;
        }

        if (jsonNode is null)
            return root;

        var result = NavigateJsonPath(jsonNode, pathPart);
        return ConvertJsonNodeToObject(result);
    }

    private static JsonNode? NavigateJsonPath(JsonNode root, string pathPart)
    {
        var trimmed = pathPart.Trim();
        if (trimmed.StartsWith("."))
            trimmed = trimmed[1..];

        var current = root;
        var matches = PathSegmentRegex.Matches("." + trimmed);

        foreach (Match match in matches)
        {
            if (current is null)
                return null;

            if (match.Groups[1].Success)
            {
                var propertyName = match.Groups[1].Value;
                if (current is JsonObject obj)
                    current = obj[propertyName];
                else
                    return null;
            }
            else if (match.Groups[2].Success)
            {
                if (int.TryParse(match.Groups[2].Value, out var index) && current is JsonArray array && index >= 0 && index < array.Count)
                    current = array[index];
                else
                    return null;
            }
            else if (match.Groups[3].Success || match.Groups[4].Success)
            {
                var key = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                if (current is JsonObject obj)
                    current = obj[key];
                else
                    return null;
            }
        }

        return current;
    }

    private static object? ConvertJsonNodeToObject(JsonNode? node)
    {
        if (node is null)
            return null;

        return node switch
        {
            JsonValue value => ExtractJsonValue(value),
            // Not `array.Deserialize<List<object?>>(...)` / `obj.Deserialize<Dictionary<string,
            // object?>>(...)`: since the target element type is `object`, System.Text.Json
            // deserializes each element as a boxed JsonElement rather than a native
            // string/bool/double — so a resolved "C#" came back as a JsonElement whose ToString()
            // (or whose JsonSerializer.Serialize()) is the quoted `"C#"`, not the bare string.
            // Recursing through this same method per element keeps every leaf a native primitive.
            JsonArray array => array.Select(ConvertJsonNodeToObject).ToList(),
            JsonObject obj => obj.ToDictionary(p => p.Key, p => ConvertJsonNodeToObject(p.Value), StringComparer.OrdinalIgnoreCase),
            _ => node.ToJsonString(),
        };
    }

    private static object? ExtractJsonValue(JsonValue value)
    {
        if (value.TryGetValue<string>(out var s))
            return s;
        if (value.TryGetValue<long>(out var l))
            return l;
        if (value.TryGetValue<int>(out var i))
            return i;
        if (value.TryGetValue<double>(out var d))
            return d;
        if (value.TryGetValue<bool>(out var b))
            return b;
        return value.ToJsonString();
    }

    private static bool TrySplitNodeIdAndPath(string expression, IDictionary<string, object?> state, out string nodeId, out string? pathPart)
    {
        nodeId = string.Empty;
        pathPart = null;

        if (string.IsNullOrWhiteSpace(expression))
            return false;

        if (state.ContainsKey(expression))
        {
            nodeId = expression;
            pathPart = null;
            return true;
        }

        var parts = expression.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        string? bestMatch = null;
        var current = string.Empty;
        for (var i = 0; i < parts.Length; i++)
        {
            current = i == 0 ? parts[0] : current + "." + parts[i];
            if (state.ContainsKey(current))
                bestMatch = current;
        }

        if (string.IsNullOrEmpty(bestMatch))
            return false;

        nodeId = bestMatch;

        if (bestMatch.Length == expression.Length)
        {
            pathPart = null;
            return true;
        }

        pathPart = expression.Substring(bestMatch.Length).TrimStart();
        return true;
    }

    private static object? ResolveJsonElement(JsonElement element, IDictionary<string, object?> state)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => ResolveString(element.GetString() ?? string.Empty, state),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => ResolveJsonObjectToElement(element, state),
            JsonValueKind.Array => ResolveJsonArrayToElement(element, state),
            _ => null,
        };
    }

    private static JsonElement ResolveJsonObjectToElement(JsonElement element, IDictionary<string, object?> state)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            WriteResolvedValue(writer, ResolveJsonElement(property.Value, state));
        }
        writer.WriteEndObject();
        writer.Flush();

        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static JsonElement ResolveJsonArrayToElement(JsonElement element, IDictionary<string, object?> state)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);

        writer.WriteStartArray();
        foreach (var item in element.EnumerateArray())
            WriteResolvedValue(writer, ResolveJsonElement(item, state));
        writer.WriteEndArray();
        writer.Flush();

        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static void WriteResolvedValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement je:
                je.WriteTo(writer);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            default:
                JsonSerializer.Serialize(writer, value);
                break;
        }
    }

    private static object? ResolveJsonNode(JsonNode node, IDictionary<string, object?> state)
    {
        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var s) => ResolveString(s, state),
            JsonValue value => ExtractJsonValue(value),
            JsonObject obj => ResolveJsonObjectNodeToElement(obj, state),
            JsonArray arr => ResolveJsonArrayNodeToElement(arr, state),
            _ => node.ToJsonString(),
        };
    }

    private static JsonElement ResolveJsonObjectNodeToElement(JsonObject obj, IDictionary<string, object?> state)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj)
            resolved[property.Key] = property.Value is not null ? ResolveJsonNode(property.Value, state) : null;
        return JsonSerializer.SerializeToElement(resolved);
    }

    private static JsonElement ResolveJsonArrayNodeToElement(JsonArray arr, IDictionary<string, object?> state)
    {
        var resolved = new List<object?>();
        foreach (var item in arr)
            resolved.Add(item is not null ? ResolveJsonNode(item, state) : null);
        return JsonSerializer.SerializeToElement(resolved);
    }

    /// <summary>
    /// Renders a resolved path's value for splicing into a surrounding text template — e.g.
    /// <c>Missing skills: {{ score_fit.response.missing_skills }}</c>. A resolved JSON array/object
    /// comes back from <see cref="NavigatePath"/> as a plain <c>List&lt;object?&gt;</c> /
    /// <c>Dictionary&lt;string, object?&gt;</c> (see <see cref="ConvertJsonNodeToObject"/>), which —
    /// before this fix — fell through to the default arm and rendered as .NET's own
    /// <c>ToString()</c> for that generic type, e.g. literally the text
    /// "System.Collections.Generic.List`1[System.Object]" instead of the array's contents
    /// (confirmed in production: a workflow's HIL review form showed exactly that string in place
    /// of a candidate's matched/missing skills). Every node that interpolates an array or object
    /// field into a text field — not just this one workflow — hit the same bug, since this
    /// resolver is shared by every node/executable that resolves state-based text input.
    /// </summary>
    private static string ConvertToString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b.ToString().ToLowerInvariant(),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            // A list of primitives (the common case — matched_skills, tags, …) reads naturally
            // joined; a list containing objects/nested arrays falls back to JSON per-element so
            // nothing is silently dropped.
            List<object?> list => string.Join(", ", list.Select(item => item is string or bool or double or int or long or null ? ConvertToString(item) : JsonSerializer.Serialize(item, SerializerOptions))),
            // A list of texts as a step gives it (NodeResult.AddTextList): joined the same way.
            IEnumerable<string> texts => string.Join(", ", texts),
            IDictionary<string, object?> dict => JsonSerializer.Serialize(dict, SerializerOptions),
            _ => value.ToString() ?? string.Empty,
        };
    }
}
