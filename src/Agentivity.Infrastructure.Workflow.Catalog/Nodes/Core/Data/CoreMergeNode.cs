using Agentivity.Domain.Workflow;
using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;

/// <summary>
/// Merges two objects into one. Properties from 'overlay' override those from 'base'.
/// Useful for combining iteration config with node outputs in foreach loops.
/// </summary>
public sealed class CoreMergeNode : ToolNode
{
    public CoreMergeNode()
        : base("core.merge")
    {
        DefineMetadata("Merge", "Merges two objects into one, with overlay properties overriding base.", "core.data", whenToUse: "To combine two JSON objects into one, the fields of the second replacing those of the first, for example default settings and the values of the current item. Not for lists: use Collect to build a list.");
        DefineEffects(NodeEffects.None);

        DefineInput(
            "base",
            ArgumentType.Text,
            "Base object to merge into",
            ui: new ArgumentUiMetadata
            {
                Group = "Objects",
                Order = 10,
                Multiline = true,
                Rows = 6,
                Format = "json",
            },
            required: false
        );
        DefineInput(
            "overlay",
            ArgumentType.Text,
            "Object whose properties override base",
            ui: new ArgumentUiMetadata
            {
                Group = "Objects",
                Order = 20,
                Multiline = true,
                Rows = 6,
                Format = "json",
            },
            required: true
        );
        DefineInput("deep", ArgumentType.Boolean, "Deep merge nested objects", false, ui: new ArgumentUiMetadata { Group = "Options", Order = 30 }, required: false);

        DefineOutput("result", ArgumentType.Text, "Merged object");

        DefineIcon(NodeIcon.Embedded("core_merge_node"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        string baseObj = args.GetText("base") ?? "{}";
        string overlayObj = args.GetText("overlay") ?? "{}";
        bool deep = args.GetBoolean("deep") ?? false;

        Dictionary<string, object?> baseDictionary = ParseJsonToDictionary(baseObj);
        Dictionary<string, object?> overlayDictionary = ParseJsonToDictionary(overlayObj);

        Dictionary<string, object?> merged = deep ? DeepMerge(baseDictionary, overlayDictionary) : ShallowMerge(baseDictionary, overlayDictionary);

        return ValueTask.FromResult(NodeResult.Create().AddText("result", System.Text.Json.JsonSerializer.Serialize(merged)));
    }

    private static Dictionary<string, object?> ParseJsonToDictionary(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, object?> ShallowMerge(Dictionary<string, object?> baseDict, Dictionary<string, object?> overlayDict)
    {
        var result = new Dictionary<string, object?>(baseDict, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, object?> kvp in overlayDict)
        {
            result[kvp.Key] = kvp.Value;
        }

        return result;
    }

    private static Dictionary<string, object?> DeepMerge(Dictionary<string, object?> baseDict, Dictionary<string, object?> overlayDict)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Copy all base properties
        foreach (KeyValuePair<string, object?> kvp in baseDict)
        {
            result[kvp.Key] = kvp.Value;
        }

        // Merge overlay properties
        foreach (KeyValuePair<string, object?> kvp in overlayDict)
        {
            if (result.TryGetValue(kvp.Key, out object? existingValue) && existingValue is Dictionary<string, object?> existingDict && kvp.Value is Dictionary<string, object?> overlayNestedDict)
            {
                // Both are dictionaries, merge recursively
                result[kvp.Key] = DeepMerge(existingDict, overlayNestedDict);
            }
            else
            {
                // Override with overlay value
                result[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    private static new Dictionary<string, object?> ToDictionary(object? value)
    {
        return value switch
        {
            null => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            Dictionary<string, object?> dict => new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase),
            IReadOnlyDictionary<string, object?> readOnly => new Dictionary<string, object?>(readOnly, StringComparer.OrdinalIgnoreCase),
            IDictionary<string, object?> idict => new Dictionary<string, object?>(idict, StringComparer.OrdinalIgnoreCase),
            JsonElement element when element.ValueKind == JsonValueKind.Object => ParseJsonElement(element),
            string json when json.TrimStart().StartsWith('{') => ParseJsonString(json),
            _ => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static Dictionary<string, object?> ParseJsonElement(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            result[property.Name] = ConvertJsonValue(property.Value);
        }

        return result;
    }

    private static Dictionary<string, object?> ParseJsonString(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return ParseJsonElement(doc.RootElement);
            }
        }
        catch
        {
            // Invalid JSON, return empty
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ParseJsonElement(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
