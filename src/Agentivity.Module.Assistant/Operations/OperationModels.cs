using System.Text.Json.Nodes;

namespace Agentivity.Module.Assistant.Operations;

/// <summary>
/// One instruction to change a draft: an operation name and its arguments. The caller (a person's tool, the assistant's
/// loop) never writes the entity document itself; the engine builds and checks it.
/// </summary>
public sealed record BuilderOperation(string Op, JsonObject Args)
{
    /// <summary>Reads <c>{ "op": "add_node", "entity": "…", … }</c>: the name and everything else as arguments.</summary>
    public static BuilderOperation From(JsonObject flat)
    {
        ArgumentNullException.ThrowIfNull(flat);
        var op = flat["op"] is JsonValue v && v.TryGetValue<string>(out var name) ? name : "";
        var args = new JsonObject();
        foreach (var (key, value) in flat)
        {
            if (!string.Equals(key, "op", StringComparison.OrdinalIgnoreCase))
                args[key] = value?.DeepClone();
        }

        return new BuilderOperation(op, args);
    }

    public string? Text(string name) => Args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    public bool Has(string name) => Args.ContainsKey(name);

    public int? Int(string name) =>
        Args[name] switch
        {
            JsonValue v when v.TryGetValue<int>(out var i) => i,
            JsonValue v when v.TryGetValue<double>(out var d) => (int)Math.Round(d),
            JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
}

/// <summary>Why an operation was refused, with what would make it work.</summary>
public sealed record OperationError(string Code, string Message, string? Hint = null, IReadOnlyList<string>? Options = null);

public sealed class OperationOutcome
{
    public int Index { get; init; }

    public string Op { get; init; } = "";

    public bool Ok { get; init; }

    /// <summary>What was done, in plain words; or, when refused, the reason.</summary>
    public string Message { get; init; } = "";

    public OperationError? Error { get; init; }

    public List<string> Warnings { get; init; } = [];

    /// <summary>What the operation produced and later operations may refer to (a new entity id, a new node id).</summary>
    public JsonObject? Produced { get; init; }
}

public sealed class BatchResult
{
    /// <summary>True when the changes were kept. A refused operation in an atomic batch leaves the changeset untouched.</summary>
    public bool Applied { get; init; }

    public bool Atomic { get; init; }

    public int Revision { get; init; }

    public List<OperationOutcome> Results { get; init; } = [];

    public bool AllOk => Results.All(r => r.Ok);
}
