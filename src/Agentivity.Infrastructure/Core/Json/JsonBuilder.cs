using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Core.Json;

/// <summary>
/// Provides utility methods for building and manipulating JsonNode structures.
/// </summary>
public static class JsonBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Creates a new JsonObject with case-insensitive property access.
    /// </summary>
    public static JsonObject CreateObject() => new();

    /// <summary>
    /// Creates a new JsonObject from key-value pairs.
    /// </summary>
    public static JsonObject CreateObject(params (string Key, object? Value)[] properties)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in properties)
        {
            obj[key] = ToJsonNode(value);
        }
        return obj;
    }

    /// <summary>
    /// Creates a new JsonArray.
    /// </summary>
    public static JsonArray CreateArray() => new();

    /// <summary>
    /// Creates a new JsonArray from items.
    /// </summary>
    public static JsonArray CreateArray(params object?[] items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(ToJsonNode(item));
        }
        return array;
    }

    /// <summary>
    /// Creates a new JsonArray from a string enumerable.
    /// </summary>
    public static JsonArray CreateStringArray(IEnumerable<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(JsonValue.Create(item));
        }
        return array;
    }

    /// <summary>
    /// Converts an object to a JsonNode.
    /// If the value is already a JsonNode with a parent, it will be cloned.
    /// </summary>
    public static JsonNode? ToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node when node.Parent is not null => Clone(node), // Clone if already has a parent
            JsonNode node => node,
            string s => JsonValue.Create(s),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            float f => JsonValue.Create(f),
            bool b => JsonValue.Create(b),
            IEnumerable<string> strings => CreateStringArray(strings),
            _ => JsonSerializer.SerializeToNode(value, SerializerOptions),
        };
    }

    /// <summary>
    /// Sets a property on a JsonObject, creating nested objects as needed.
    /// </summary>
    public static void SetProperty(JsonObject obj, string key, object? value)
    {
        obj[key] = ToJsonNode(value);
    }

    /// <summary>
    /// Adds a validation result to a section node.
    /// </summary>
    public static void AddValidation(JsonObject section, JsonObject validation)
    {
        section["validation"] = validation;
    }

    /// <summary>
    /// Clones a JsonObject (shallow copy of structure, deep copy of values).
    /// </summary>
    public static JsonObject Clone(JsonObject source)
    {
        var json = source.ToJsonString();
        return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
    }

    /// <summary>
    /// Clones a JsonNode.
    /// </summary>
    public static JsonNode? Clone(JsonNode? source)
    {
        if (source is null)
            return null;
        var json = source.ToJsonString();
        return JsonNode.Parse(json);
    }

    /// <summary>
    /// Serializes a JsonNode to a JSON string.
    /// </summary>
    public static string Serialize(JsonNode? node) => node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";

    /// <summary>
    /// Serializes an object to a JSON string.
    /// </summary>
    public static string Serialize(object? value) => JsonSerializer.Serialize(value, SerializerOptions);

    /// <summary>
    /// Merges properties from source into target.
    /// </summary>
    public static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var kvp in source)
        {
            target[kvp.Key] = Clone(kvp.Value);
        }
    }
}
