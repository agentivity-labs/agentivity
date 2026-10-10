using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Agentivity.Infrastructure.Core.Json;

/// <summary>
/// A JSON converter factory that automatically detects and parses JSON strings into native objects.
/// This eliminates the need for multi-level JSON escaping (JSON-in-JSON-in-JSON).
///
/// <para>
/// <b>Behavior:</b>
/// <list type="bullet">
///   <item>String values starting with <c>{</c> or <c>[</c> are parsed as JSON objects/arrays</item>
///   <item>Strings containing template expressions <c>{{ }}</c> are kept as strings for later resolution</item>
///   <item>Invalid JSON strings are kept as-is</item>
///   <item>All other types are handled normally</item>
/// </list>
/// </para>
/// </summary>
public class SmartJsonStringConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        // Handle Dictionary<string, object?> and IReadOnlyDictionary<string, object?>
        if (typeToConvert.IsGenericType)
        {
            var genericDef = typeToConvert.GetGenericTypeDefinition();
            if (genericDef == typeof(Dictionary<,>) || genericDef == typeof(IReadOnlyDictionary<,>) || genericDef == typeof(IDictionary<,>))
            {
                var args = typeToConvert.GetGenericArguments();
                return args.Length == 2 && args[0] == typeof(string) && args[1] == typeof(object);
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return new SmartDictionaryConverter();
    }
}

/// <summary>
/// Converter for Dictionary&lt;string, object?&gt; that applies smart JSON string detection.
/// </summary>
internal sealed class SmartDictionaryConverter : JsonConverter<IReadOnlyDictionary<string, object?>>
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (typeToConvert.IsGenericType)
        {
            var genericDef = typeToConvert.GetGenericTypeDefinition();
            if (genericDef == typeof(Dictionary<,>) || genericDef == typeof(IReadOnlyDictionary<,>) || genericDef == typeof(IDictionary<,>))
            {
                var args = typeToConvert.GetGenericArguments();
                return args.Length == 2 && args[0] == typeof(string) && args[1] == typeof(object);
            }
        }

        return false;
    }

    public override IReadOnlyDictionary<string, object?>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected StartObject, got {reader.TokenType}");
        }

        var node = JsonNode.Parse(ref reader);

        if (node is JsonObject obj)
        {
            return SmartJsonHelper.ConvertJsonObjectWithSmartStrings(obj);
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var (key, val) in value)
        {
            writer.WritePropertyName(key);
            SmartJsonHelper.WriteValue(writer, val, options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Helper class with static methods for smart JSON conversion.
/// </summary>
internal static class SmartJsonHelper
{
    /// <summary>
    /// Recursively converts a JsonObject to a dictionary, applying smart string conversion.
    /// </summary>
    public static Dictionary<string, object?> ConvertJsonObjectWithSmartStrings(JsonObject obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in obj)
        {
            result[key] = ConvertJsonNodeWithSmartStrings(value);
        }

        return result;
    }

    /// <summary>
    /// Recursively converts a JsonArray to a list, applying smart string conversion.
    /// </summary>
    public static List<object?> ConvertJsonArrayWithSmartStrings(JsonArray arr)
    {
        var result = new List<object?>(arr.Count);

        foreach (var item in arr)
        {
            result.Add(ConvertJsonNodeWithSmartStrings(item));
        }

        return result;
    }

    /// <summary>
    /// Converts a JsonNode to its appropriate .NET type with smart string handling.
    /// </summary>
    public static object? ConvertJsonNodeWithSmartStrings(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node switch
        {
            JsonObject obj => ConvertJsonObjectWithSmartStrings(obj),
            JsonArray arr => ConvertJsonArrayWithSmartStrings(arr),
            JsonValue val => ConvertJsonValueWithSmartStrings(val),
            _ => node.ToJsonString(),
        };
    }

    /// <summary>
    /// Converts a JsonValue to its appropriate .NET type, with smart JSON string detection.
    /// </summary>
    private static object? ConvertJsonValueWithSmartStrings(JsonValue value)
    {
        // Try to extract the value in order of preference
        if (value.TryGetValue<string>(out var s))
        {
            // Apply smart string conversion
            if (!string.IsNullOrWhiteSpace(s) && TryParseEmbeddedJson(s, out var parsed))
            {
                return parsed;
            }

            return s;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<decimal>(out var dec))
        {
            return dec;
        }

        return value.ToJsonString();
    }

    /// <summary>
    /// Attempts to parse a string as embedded JSON.
    /// Returns true if the string is valid JSON that was successfully parsed.
    /// Template expressions ({{ }}) inside the JSON are preserved as string values.
    /// </summary>
    public static bool TryParseEmbeddedJson(string value, out object? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        // Must start with { or [ to be considered JSON
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return false;
        }

        // Note: We DO parse JSON even if it contains {{ }} templates.
        // The templates are VALUES inside the JSON, not the JSON itself.
        // They will be preserved as string values and resolved later by WorkflowValueResolver.

        try
        {
            var node = JsonNode.Parse(trimmed);

            if (node is JsonObject obj)
            {
                result = ConvertJsonObjectWithSmartStrings(obj);
                return true;
            }

            if (node is JsonArray arr)
            {
                result = ConvertJsonArrayWithSmartStrings(arr);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if a string contains template expressions ({{ ... }}).
    /// </summary>
    public static bool ContainsTemplateExpression(string value)
    {
        return value.Contains("{{", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes a value to a JSON writer with proper type handling.
    /// </summary>
    public static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value)
        {
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
            case float f:
                writer.WriteNumberValue(f);
                break;
            case Dictionary<string, object?> dict:
                writer.WriteStartObject();
                foreach (var (k, v) in dict)
                {
                    writer.WritePropertyName(k);
                    WriteValue(writer, v, options);
                }
                writer.WriteEndObject();
                break;
            case IReadOnlyDictionary<string, object?> roDict:
                writer.WriteStartObject();
                foreach (var (k, v) in roDict)
                {
                    writer.WritePropertyName(k);
                    WriteValue(writer, v, options);
                }
                writer.WriteEndObject();
                break;
            case List<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    WriteValue(writer, item, options);
                }
                writer.WriteEndArray();
                break;
            case IList<object?> iList:
                writer.WriteStartArray();
                foreach (var item in iList)
                {
                    WriteValue(writer, item, options);
                }
                writer.WriteEndArray();
                break;
            case JsonNode node:
                node.WriteTo(writer, options);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                break;
        }
    }
}

/// <summary>
/// Backward compatibility alias for SmartJsonStringConverterFactory.
/// </summary>
[Obsolete("Use SmartJsonStringConverterFactory instead")]
public class SmartJsonStringConverter : SmartJsonStringConverterFactory { }
