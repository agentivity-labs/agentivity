using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.AgUi;

namespace Agentivity.Infrastructure.Workflows.Resources;

public sealed class ResourceBinding : IResourceBindingReader
{
    private readonly IDictionary<string, object?>? _state;

    public ResourceBinding(string sourceNodeId, string sourcePort, WorkflowNodeDefinition sourceNode, string resourceType, IDictionary<string, object?>? state = null, WorkflowEntity? definition = null)
    {
        SourceNodeId = sourceNodeId ?? throw new ArgumentNullException(nameof(sourceNodeId));
        SourcePort = string.IsNullOrWhiteSpace(sourcePort) ? throw new ArgumentException("Source port is required", nameof(sourcePort)) : sourcePort;
        SourceNode = sourceNode ?? throw new ArgumentNullException(nameof(sourceNode));
        ResourceType = string.IsNullOrWhiteSpace(resourceType) ? throw new ArgumentException("Resource type is required", nameof(resourceType)) : resourceType;
        _state = state;
        Definition = definition;
    }

    public string SourceNodeId { get; }

    public string SourcePort { get; }

    public WorkflowNodeDefinition SourceNode { get; }

    public string ResourceType { get; }

    /// <summary>
    /// The full workflow graph, when available — lets a resource node resolve ITS OWN resource
    /// input ports (e.g. a vector store with an optional "embeddings" port) from inside methods
    /// that only receive a <see cref="ResourceBinding"/>, not a <see cref="NodeConfig"/>. Null for
    /// bindings constructed without graph context (e.g. <c>AgUiCapabilityResolver</c>'s probes).
    /// </summary>
    public WorkflowEntity? Definition { get; }

    /// <summary>Shared run state, when available — see <see cref="Definition"/> for why this is exposed.</summary>
    public IDictionary<string, object?>? State => _state;

    /// <summary>
    /// Gets the raw JSON parameters from the source node.
    /// </summary>
    public JsonElement Parameters => SourceNode.Parameters;

    /// <summary>
    /// Gets a parameter value by name.
    /// </summary>
    private JsonElement? GetParameter(string key)
    {
        return SourceNode.GetParameter(key);
    }

    public string? GetString(string key)
    {
        var value = GetParameter(key);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string? result;

        // Resolve template expressions using WorkflowValueResolver if state is available
        if (_state != null)
        {
            var resolved = ArgumentValueResolver.Resolve(value.Value, _state);
            result = resolved?.ToString();
        }
        else
        {
            // Fallback: raw value without resolution
            result = value.Value.ValueKind switch
            {
                JsonValueKind.String => value.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.Value.ToString(),
                _ => value.Value.ToString(),
            };
        }

        // Treat empty/whitespace as "no value" so callers can use ?? for defaults
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public string RequireString(string key)
    {
        return GetString(key) ?? throw new InvalidOperationException($"Resource node '{SourceNodeId}' is missing required parameter '{key}'.");
    }

    public IReadOnlyList<string> GetStringList(string key)
    {
        var value = GetParameter(key);
        if (value is null)
        {
            return [];
        }

        if (_state != null)
        {
            var resolved = ArgumentValueResolver.Resolve(value.Value, _state);
            return ConvertToStringList(resolved);
        }

        return ConvertToStringList(value.Value);
    }

    public JsonElement? GetStructured(string key)
    {
        var value = GetParameter(key);
        if (value is null)
        {
            return null;
        }

        if (_state != null)
        {
            var resolved = ArgumentValueResolver.Resolve(value.Value, _state);
            return ConvertToStructuredElement(resolved);
        }

        return value.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? value.Value.Clone() : null;
    }

    public int? GetInt(string key)
    {
        var value = GetParameter(key);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // Resolve template expressions first if state is available
        if (_state != null)
        {
            var resolved = ArgumentValueResolver.Resolve(value.Value, _state);
            if (resolved is int i)
                return i;
            if (resolved is long l)
                return (int)l;
            if (resolved is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return value.Value.ValueKind switch
        {
            // TryGetInt32 rejects any number token with a decimal point (even a whole one like
            // "10000.0") — several UI number inputs round-trip clean integers this way, so fall
            // back to a whole-number double read rather than silently dropping the value.
            JsonValueKind.Number => value.Value.TryGetInt32(out var i) ? i
                : value.Value.TryGetDouble(out var d) && d % 1 == 0 && d is >= int.MinValue and <= int.MaxValue ? (int)d
                : (int?)null,
            JsonValueKind.String when int.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => null,
        };
    }

    public double? GetDouble(string key)
    {
        var value = GetParameter(key);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // Resolve template expressions first if state is available
        if (_state != null)
        {
            var resolved = ArgumentValueResolver.Resolve(value.Value, _state);
            if (resolved is double d)
                return d;
            if (resolved is int i)
                return i;
            if (resolved is long l)
                return l;
            if (resolved is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.Number => value.Value.TryGetDouble(out var d) ? d : (double?)null,
            JsonValueKind.String when double.TryParse(value.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public bool? GetBool(string key)
    {
        var value = GetParameter(key);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        // Resolve template expressions first if state is available
        if (_state != null)
        {
            var resolved = ArgumentValueResolver.Resolve(value.Value, _state);
            if (resolved is bool b)
                return b;
            if (resolved is string s && bool.TryParse(s, out var parsed))
                return parsed;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.Value.GetString(), out var b) => b,
            _ => null,
        };
    }

    public object? GetStateValue(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_state is null)
        {
            return null;
        }

        return _state.TryGetValue(key, out var value) ? value : null;
    }

    public void SetStateValue(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_state is null)
        {
            return;
        }

        _state[key] = value;
    }

    private static IReadOnlyList<string> ConvertToStringList(object? raw)
    {
        if (raw is null)
        {
            return [];
        }

        if (raw is string s)
        {
            return string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()];
        }

        if (raw is IReadOnlyList<string> readOnlyList)
        {
            return readOnlyList.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList();
        }

        if (raw is IEnumerable<string> stringEnumerable)
        {
            return stringEnumerable.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList();
        }

        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!.Trim()).ToList();
        }

        if (raw is IEnumerable<object?> objectEnumerable)
        {
            return objectEnumerable.Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!.Trim()).ToList();
        }

        return [];
    }

    private static JsonElement? ConvertToStructuredElement(object? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (raw is JsonElement element)
        {
            return element.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? element.Clone() : null;
        }

        if (raw is JsonNode node)
        {
            var elementFromNode = JsonSerializer.SerializeToElement(node);
            return elementFromNode.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? elementFromNode : null;
        }

        if (raw is IDictionary<string, object?> or IList<object?> or IEnumerable<object?>)
        {
            var serialized = JsonSerializer.SerializeToElement(raw);
            return serialized.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? serialized : null;
        }

        return null;
    }
}
