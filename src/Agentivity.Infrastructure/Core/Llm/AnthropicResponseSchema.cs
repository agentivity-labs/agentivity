using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;

namespace Agentivity.Infrastructure.Core.Llm;

/// <summary>
/// Turns a platform response schema into what Anthropic's native structured outputs
/// (<c>output_config.format</c>, type <c>json_schema</c>) accepts. The platform's schemas are written
/// for the widest set of backends; Anthropic accepts a subset of JSON Schema: no numeric or
/// length/size constraints, <c>anyOf</c> but not <c>oneOf</c>, and every object closed with
/// <c>additionalProperties: false</c>. The official Anthropic SDKs do the same clean-up before
/// sending; sending the raw schema instead would turn a request that works today (the schema was
/// simply ignored) into an HTTP 400.
/// <para>
/// A constraint dropped here is no longer enforced by the API — the caller still reads the answer
/// defensively, exactly as it always did.
/// </para>
/// </summary>
internal static class AnthropicResponseSchema
{
    // Keywords Anthropic's structured outputs reject. Only ever removed where they are SCHEMA keywords,
    // never where they are property names (a property called "minimum" lives under "properties").
    private static readonly string[] UnsupportedKeywords =
    [
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength",
        "minItems", "maxItems", "uniqueItems",
        "minProperties", "maxProperties",
    ];

    /// <summary>Parses <paramref name="responseSchema"/> and cleans it for Anthropic; a failure names what was wrong.</summary>
    public static OperationResult<JsonNode> Build(string responseSchema)
    {
        try
        {
            var node = JsonNode.Parse(responseSchema);
            if (node is not JsonObject schema)
                return OperationResult<JsonNode>.Failure("Invalid response schema: the schema must be a JSON object.");

            Sanitize(schema);
            return OperationResult<JsonNode>.Success(schema);
        }
        catch (JsonException ex)
        {
            return OperationResult<JsonNode>.Failure($"Invalid response schema: {ex.Message}");
        }
    }

    /// <summary>
    /// A tool's input schema cleaned the same way and, when EVERY object in it is closed, returned for use with
    /// <c>strict: true</c>; null when the schema cannot be strict (a free-form object such as a column bag, or no
    /// declared properties) — that tool is then sent as it is, without <c>strict</c>.
    /// </summary>
    public static JsonNode? TryMakeStrict(JsonNode inputSchema)
    {
        if (inputSchema.DeepClone() is not JsonObject clone)
            return null;

        Sanitize(clone);
        return IsFullyClosed(clone) ? clone : null;
    }

    private static bool IsFullyClosed(JsonObject schema)
    {
        var isObject = schema["type"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var type) => type == "object",
            JsonArray types => types.Any(t => t?.GetValue<string>() == "object"),
            _ => schema["properties"] is JsonObject,
        };

        if (isObject)
        {
            if (schema["properties"] is not JsonObject properties)
                return false;
            if (schema["additionalProperties"] is not JsonValue { } additional || !additional.TryGetValue<bool>(out var open) || open)
                return false;
            if (properties.Any(p => p.Value is JsonObject child && !IsFullyClosed(child)))
                return false;
        }

        if (schema["items"] is JsonObject items && !IsFullyClosed(items))
            return false;

        foreach (var combinator in new[] { "anyOf", "allOf" })
        {
            if (schema[combinator] is JsonArray alternatives && alternatives.Any(a => a is JsonObject alt && !IsFullyClosed(alt)))
                return false;
        }

        foreach (var container in new[] { "$defs", "definitions" })
        {
            if (schema[container] is JsonObject definitions && definitions.Any(d => d.Value is JsonObject def && !IsFullyClosed(def)))
                return false;
        }

        return true;
    }

    private static void Sanitize(JsonObject schema)
    {
        foreach (var keyword in UnsupportedKeywords)
            schema.Remove(keyword);

        // oneOf -> anyOf: for the answers these schemas describe the alternatives are exclusive anyway.
        if (schema.Remove("oneOf", out var oneOf) && oneOf is not null && !schema.ContainsKey("anyOf"))
            schema["anyOf"] = oneOf;

        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (_, property) in properties)
            {
                if (property is JsonObject propertySchema)
                    Sanitize(propertySchema);
            }

            // Anthropic requires closed objects. An object that declares its properties is closed;
            // a free-form object (no properties) cannot be expressed and is left as it is.
            if (!schema.TryGetPropertyValue("additionalProperties", out var additional) || additional is not JsonValue { } value || !value.TryGetValue<bool>(out var open) || open)
                schema["additionalProperties"] = false;
        }

        foreach (var container in new[] { "$defs", "definitions" })
        {
            if (schema[container] is JsonObject definitions)
            {
                foreach (var (_, definition) in definitions)
                {
                    if (definition is JsonObject definitionSchema)
                        Sanitize(definitionSchema);
                }
            }
        }

        if (schema["items"] is JsonObject items)
            Sanitize(items);

        foreach (var combinator in new[] { "anyOf", "allOf" })
        {
            if (schema[combinator] is JsonArray alternatives)
            {
                foreach (var alternative in alternatives)
                {
                    if (alternative is JsonObject alternativeSchema)
                        Sanitize(alternativeSchema);
                }
            }
        }
    }
}
