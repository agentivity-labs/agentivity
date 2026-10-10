using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Core.Json;

/// <summary>
/// Provides utility methods for navigating and extracting values from JsonNode structures.
/// </summary>
public static class JsonNavigator
{
    /// <summary>
    /// Gets a string value from a JsonNode by key.
    /// </summary>
    public static string? GetString(JsonNode? node, string key) => node?[key]?.GetValue<string>();

    /// <summary>
    /// Gets a string value from a JsonNode by key, with a default fallback.
    /// </summary>
    public static string GetString(JsonNode? node, string key, string defaultValue) => node?[key]?.GetValue<string>() ?? defaultValue;

    /// <summary>
    /// Gets an integer value from a JsonNode by key.
    /// </summary>
    public static int? GetInt(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null)
            return null;

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<int>(out var i))
                return i;
            if (jsonValue.TryGetValue<long>(out var l))
                return (int)l;
            if (jsonValue.TryGetValue<double>(out var d))
                return (int)d;
        }

        return null;
    }

    /// <summary>
    /// Gets an integer value from a JsonNode by key, with a default fallback.
    /// </summary>
    public static int GetInt(JsonNode? node, string key, int defaultValue) => GetInt(node, key) ?? defaultValue;

    /// <summary>
    /// Gets a boolean value from a JsonNode by key.
    /// </summary>
    public static bool? GetBool(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null)
            return null;

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var b))
        {
            return b;
        }

        return null;
    }

    /// <summary>
    /// Gets a boolean value from a JsonNode by key, with a default fallback.
    /// </summary>
    public static bool GetBool(JsonNode? node, string key, bool defaultValue) => GetBool(node, key) ?? defaultValue;

    /// <summary>
    /// Gets a double value from a JsonNode by key.
    /// </summary>
    public static double? GetDouble(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null)
            return null;

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<double>(out var d))
                return d;
            if (jsonValue.TryGetValue<int>(out var i))
                return i;
            if (jsonValue.TryGetValue<long>(out var l))
                return l;
            if (jsonValue.TryGetValue<float>(out var f))
                return f;
        }

        return null;
    }

    /// <summary>
    /// Gets a double value from a JsonNode by key, with a default fallback.
    /// </summary>
    public static double GetDouble(JsonNode? node, string key, double defaultValue) => GetDouble(node, key) ?? defaultValue;

    /// <summary>
    /// Gets a JsonArray from a JsonNode by key.
    /// </summary>
    public static JsonArray? GetArray(JsonNode? node, string key) => node?[key]?.AsArray();

    /// <summary>
    /// Gets a JsonObject from a JsonNode by key.
    /// </summary>
    public static JsonObject? GetObject(JsonNode? node, string key) => node?[key]?.AsObject();

    /// <summary>
    /// Gets the children array from a section node.
    /// </summary>
    public static JsonArray GetChildren(JsonNode? node) => node?["children"]?.AsArray() ?? new JsonArray();

    /// <summary>
    /// Checks if a node has children.
    /// </summary>
    public static bool HasChildren(JsonNode? node)
    {
        var children = node?["children"]?.AsArray();
        return children is not null && children.Count > 0;
    }

    /// <summary>
    /// Gets a list of strings from a JsonArray node.
    /// </summary>
    public static List<string> GetStringList(JsonNode? node, string key)
    {
        var array = node?[key]?.AsArray();
        if (array is null)
            return new List<string>();

        return array.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
    }

    /// <summary>
    /// Extracts a primitive value from a JsonValue node.
    /// </summary>
    public static object? ExtractValue(JsonValue? value)
    {
        if (value is null)
            return null;

        if (value.TryGetValue<string>(out var s))
            return s;
        if (value.TryGetValue<int>(out var i))
            return i;
        if (value.TryGetValue<long>(out var l))
            return l;
        if (value.TryGetValue<double>(out var d))
            return d;
        if (value.TryGetValue<bool>(out var b))
            return b;

        return value.ToJsonString();
    }

    /// <summary>
    /// Converts any JsonNode value to a double.
    /// </summary>
    public static double ToDouble(JsonNode? node)
    {
        if (node is null)
            return 0.0;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d))
                return d;
            if (value.TryGetValue<int>(out var i))
                return i;
            if (value.TryGetValue<long>(out var l))
                return l;
            if (value.TryGetValue<float>(out var f))
                return f;
        }

        return 0.0;
    }

    /// <summary>
    /// Checks if a JsonNode value is truthy.
    /// </summary>
    public static bool IsTrue(JsonNode? node)
    {
        if (node is null)
            return false;

        if (node is JsonValue value && value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        return false;
    }

    /// <summary>
    /// Safely parses a JSON string into a JsonNode.
    /// </summary>
    public static JsonNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Safely parses a JSON string into a JsonArray.
    /// </summary>
    public static JsonArray? TryParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var node = JsonNode.Parse(json);
            return node?.AsArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Safely parses a JSON string into a JsonObject.
    /// </summary>
    public static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var node = JsonNode.Parse(json);
            return node?.AsObject();
        }
        catch
        {
            return null;
        }
    }
}
