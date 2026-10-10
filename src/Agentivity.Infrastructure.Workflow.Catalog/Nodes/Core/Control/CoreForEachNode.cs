using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Control;

/// <summary>
/// Control node that iterates over a collection.
/// Implements IControlFlowNode for engine-managed loop execution.
/// </summary>
public sealed class CoreForEachNode : ControlFlowNode
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private readonly ILogger<CoreForEachNode> _logger;

    public CoreForEachNode(ILogger<CoreForEachNode> logger)
        : base("core.foreach")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("For Each", "Iterates over a collection, executing connected nodes for each item.", "core.control");

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Single input port
        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point to start iteration", maxConnections: 1);

        // Output ports: loop for iteration, out for completion
        ClearOutputPorts();
        DefineOutputPort("loop", "Loop", "Execute for each item in collection");
        DefineOutputPort("out", "Out", "Continue after all iterations complete");

        // Data inputs
        DefineInput(
            "collection",
            ArgumentType.Text,
            "JSON array or state key to iterate over",
            ui: new ArgumentUiMetadata
            {
                Group = "Loop",
                Order = 10,
                Multiline = true,
                Rows = 6,
                Format = "json",
            }
        );

        // Data outputs
        DefineOutput("current_item", ArgumentType.Text, "Current iteration item");
        DefineOutput("current_index", ArgumentType.Number, "Current iteration index (0-based)");
        DefineOutput("total_count", ArgumentType.Number, "Total number of items in collection");
        DefineOutput("is_first", ArgumentType.Boolean, "True if this is the first item");
        DefineOutput("is_last", ArgumentType.Boolean, "True if this is the last item");
        DefineOutput("results", ArgumentType.Text, "All iteration results (after completion)");
        DefineOutput("count", ArgumentType.Number, "Number of completed iterations");

        DefineIcon(NodeIcon.Embedded("loop"));
    }

    /// <summary>
    /// Evaluates the loop and returns control flow instructions for the engine.
    /// </summary>
    public override ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        _ = GetInputs(config);

        // Get collection - can be JsonElement, string, or other types
        object? collectionRaw = config.Resolve("collection");
        if (collectionRaw is null)
        {
            throw new InvalidOperationException("Missing required argument 'collection'.");
        }

        // Parse collection based on type
        List<object?> items = ParseCollectionFromValue(collectionRaw);

        if (items.Count == 0)
        {
            services.PublishStatus("ForEach: Collection is empty, skipping to 'out' port.");

            var emptyOutputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["current_item"] = null,
                ["current_index"] = -1,
                ["total_count"] = 0,
                ["is_first"] = false,
                ["is_last"] = false,
                ["results"] = new List<object?>(),
                ["count"] = 0,
            };

            return ValueTask.FromResult(ControlFlowResult.LoopComplete("out", emptyOutputs));
        }

        services.PublishStatus($"ForEach: Starting loop with {items.Count} items");

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["total_count"] = items.Count };

        // Return loop control flow - the engine will handle iteration
        return ValueTask.FromResult(ControlFlowResult.Loop(items: items, stateKeyPrefix: config.Node.Id, loopPort: "loop", completePort: "out", outputs: outputs));
    }

    /// <summary>
    /// Parses collection from any supported NodeResult type.
    /// </summary>
    private static List<object?> ParseCollectionFromValue(object value)
    {
        return value switch
        {
            // JsonElement array (from NodeResult)
            JsonElement { ValueKind: JsonValueKind.Array } je => ParseJsonElementArray(je),

            // String (JSON or comma-separated)
            string s => ParseCollection(s),

            // Native list
            List<object?> list => list,
            IEnumerable<object?> enumerable => enumerable.ToList(),

            // Single item
            _ => [value],
        };
    }

    private static List<object?> ParseJsonElementArray(JsonElement element)
    {
        var result = new List<object?>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            result.Add(ParseJsonElement(item));
        }
        return result;
    }

    private static List<object?> ParseCollection(string collectionRaw)
    {
        if (string.IsNullOrWhiteSpace(collectionRaw))
        {
            return [];
        }

        string trimmed = collectionRaw.Trim();

        // Try parsing as JSON array
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                var node = JsonNode.Parse(trimmed);
                if (node is JsonArray array)
                {
                    return array.Select(n => ParseJsonNode(n)).ToList();
                }
            }
            catch
            {
                // Not valid JSON array
            }
        }

        // Try parsing as JsonElement
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var result = new List<object?>();
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    result.Add(ParseJsonElement(element));
                }
                return result;
            }
        }
        catch
        {
            // Not valid JSON
        }

        // Fallback: treat as single item
        return [collectionRaw];
    }

    private static object? ParseJsonNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, JsonNode?> prop in obj)
            {
                dict[prop.Key] = ParseJsonNode(prop.Value);
            }
            return dict;
        }

        if (node is JsonArray arr)
        {
            return arr.Select(ParseJsonNode).ToList();
        }

        if (node is JsonValue val)
        {
            return val.TryGetValue<string>(out string? s) ? s
                : val.TryGetValue<long>(out long l) ? l
                : val.TryGetValue<double>(out double d) ? d
                : val.TryGetValue<bool>(out bool b) ? b
                : val.ToString();
        }

        return node.ToString();
    }

    private static object? ParseJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ParseJsonObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ParseJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static Dictionary<string, object?> ParseJsonObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            dict[prop.Name] = ParseJsonElement(prop.Value);
        }
        return dict;
    }
}
