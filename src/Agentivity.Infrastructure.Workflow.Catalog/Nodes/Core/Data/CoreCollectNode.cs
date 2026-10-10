using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Data;

/// <summary>
/// Collects items into an array across multiple executions.
/// Useful for accumulating results in a foreach loop.
/// </summary>
public sealed class CoreCollectNode : ToolNode
{
    public CoreCollectNode()
        : base("core.collect")
    {
        DefineMetadata("Collect", "Collects items into an array across multiple executions.", "core.data");

        DefineInput(
            "item",
            ArgumentType.Text,
            "Item to add to the collection",
            ui: new ArgumentUiMetadata
            {
                Group = "Collection",
                Order = 10,
                Multiline = true,
                Rows = 4,
            }
        );
        DefineInput("reset", ArgumentType.Boolean, "Clear collection before adding", false, ui: new ArgumentUiMetadata { Group = "Options", Order = 20 });

        DefineOutput("items", ArgumentType.Text, "All collected items as an array");
        DefineOutput("count", ArgumentType.Number, "Number of items in collection");

        DefineIcon(NodeIcon.Embedded("core_collect_node"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        object? item = args.GetRaw("item");
        bool reset = args.GetBoolean("reset") ?? false;

        // State key unique to this node instance
        string stateKey = $"{config.Node.Id}.__collected_items";

        // Get or create the collection - stored as JSON in WorkflowTextValue
        List<object?> items;
        if (reset)
        {
            items = [];
        }
        else if (config.State.TryGetValue(stateKey, out object? existing) && existing is string textValue)
        {
            try
            {
                items = System.Text.Json.JsonSerializer.Deserialize<List<object?>>(textValue) ?? [];
            }
            catch
            {
                items = [];
            }
        }
        else
        {
            items = [];
        }

        // Add the item if provided
        if (item is not null)
        {
            items.Add(item);
        }

        // Store back in state as JSON
        config.State[stateKey] = System.Text.Json.JsonSerializer.Serialize(items);

        return ValueTask.FromResult(NodeResult.Create().AddText("items", System.Text.Json.JsonSerializer.Serialize(items)).AddNumber("count", items.Count));
    }
}
