using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Arguments;

namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// Strongly-typed result container for node outputs.
/// Only allows types aligned with <see cref="ArgumentType"/>:
/// - string (Text, Choice, Credentials — structured JSON data is serialized as string via JsonSerializer.Serialize)
/// - int, long, double, decimal (Number)
/// - bool (Boolean)
/// - ImageRef (ImageRef)
/// - WorkflowImagesRef (ImagesRef)
/// - IReadOnlyList&lt;string&gt; (TextList)
/// </summary>
public sealed class NodeResult : IDictionary<string, object?>
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    private NodeResult() { }

    public static NodeResult Create() => new();

    // ── ArgumentType.Text / Choice / Credentials ──
    public NodeResult AddText(string key, string? value)
    {
        _values[key] = value;
        return this;
    }

    // ── ArgumentType.Number ──
    public NodeResult AddNumber(string key, int value)
    {
        _values[key] = (long)value;
        return this;
    }

    public NodeResult AddNumber(string key, long value)
    {
        _values[key] = value;
        return this;
    }

    public NodeResult AddNumber(string key, double value)
    {
        _values[key] = value;
        return this;
    }

    // ── ArgumentType.Boolean ──
    public NodeResult AddBoolean(string key, bool value)
    {
        _values[key] = value;
        return this;
    }

    // ── ArgumentType.TextList ──
    public NodeResult AddTextList(string key, IReadOnlyList<string> value)
    {
        _values[key] = value;
        return this;
    }

    // ── ArgumentType.ImageRef ──
    public NodeResult AddImageRef(string key, ImageRef? value)
    {
        _values[key] = value;
        return this;
    }

    // ── ArgumentType.ImagesRef ──
    public NodeResult AddImagesRef(string key, WorkflowImagesRef? value)
    {
        _values[key] = value;
        return this;
    }

    // ── ArgumentType.AudioRef ──
    public NodeResult AddAudioRef(string key, AudioRef? value)
    {
        _values[key] = value;
        return this;
    }

    // IDictionary implementation
    public object? this[string key]
    {
        get => _values[key];
        set => ValidateAndAdd(key, value);
    }

    public ICollection<string> Keys => _values.Keys;
    public ICollection<object?> Values => _values.Values;
    public int Count => _values.Count;
    public bool IsReadOnly => false;

    public void Add(string key, object? value) => ValidateAndAdd(key, value);

    public void Add(KeyValuePair<string, object?> item) => ValidateAndAdd(item.Key, item.Value);

    public void Clear() => _values.Clear();

    public bool Contains(KeyValuePair<string, object?> item) => ((IDictionary<string, object?>)_values).Contains(item);

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex) => ((IDictionary<string, object?>)_values).CopyTo(array, arrayIndex);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

    public bool Remove(string key) => _values.Remove(key);

    public bool Remove(KeyValuePair<string, object?> item) => ((IDictionary<string, object?>)_values).Remove(item);

    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _values.GetEnumerator();

    private void ValidateAndAdd(string key, object? value)
    {
        if (value is null or string or int or long or double or decimal or bool or ImageRef or WorkflowImagesRef or IReadOnlyList<string>)
        {
            _values[key] = value;
            return;
        }

        throw new InvalidOperationException(
            $"Invalid output type '{value.GetType().Name}' for key '{key}'. "
                + "Node outputs must be: string, int, long, double, decimal, bool, IReadOnlyList<string>, ImageRef, or WorkflowImagesRef. "
                + "Structured data (JSON objects/arrays) must be serialized to string via JsonSerializer.Serialize()."
        );
    }
}
