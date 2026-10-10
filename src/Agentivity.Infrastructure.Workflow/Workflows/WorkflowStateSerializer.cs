using System.Text.Json;

namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Converts workflow state between the runtime representation (Dictionary&lt;string, object?&gt;)
/// and the persistable representation (Dictionary&lt;string, JsonElement&gt;).
/// </summary>
public static class WorkflowStateSerializer
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    /// <summary>
    /// Normalizes every value in the runtime state into a <see cref="JsonElement"/>
    /// suitable for JSON serialization.
    /// </summary>
    public static Dictionary<string, JsonElement> SerializeState(Dictionary<string, object?> state)
    {
        var result = new Dictionary<string, JsonElement>(state.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in state)
        {
            result[key] = JsonSerializer.SerializeToElement(value, SerializeOptions);
        }

        return result;
    }

    /// <summary>
    /// Converts persisted <see cref="JsonElement"/> values back to runtime primitives.
    /// <list type="bullet">
    ///   <item><see cref="JsonValueKind.String"/>  → <see cref="string"/></item>
    ///   <item><see cref="JsonValueKind.Number"/>  → <see cref="long"/> (integer) or <see cref="double"/></item>
    ///   <item><see cref="JsonValueKind.True"/>/<see cref="JsonValueKind.False"/> → <see cref="bool"/></item>
    ///   <item><see cref="JsonValueKind.Null"/>    → <c>null</c></item>
    ///   <item><see cref="JsonValueKind.Object"/>/<see cref="JsonValueKind.Array"/> → kept as <see cref="JsonElement"/>
    ///         (the <c>WorkflowValueResolver</c> already navigates <see cref="JsonElement"/>)</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, object?> DeserializeState(Dictionary<string, JsonElement> persisted)
    {
        var result = new Dictionary<string, object?>(persisted.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, element) in persisted)
        {
            result[key] = ConvertElement(element);
        }

        return result;
    }

    private static object? ConvertElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            // Object and Array stay as JsonElement — WorkflowValueResolver handles them.
            _ => element.Clone(),
        };
    }
}
