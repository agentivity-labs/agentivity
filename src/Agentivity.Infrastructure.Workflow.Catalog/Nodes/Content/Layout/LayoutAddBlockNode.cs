using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.Layout;

/// <summary>
/// Adds one or more blocks to the layout DSL at the specified target position.
/// Supports both single block and array of blocks.
/// </summary>
public sealed class LayoutAddBlockNode : ToolNode
{
    public LayoutAddBlockNode()
        : base("layout.add_block")
    {
        DefineMetadata("Add Block", "Adds one or more blocks to the layout at a specified position. Supports single block object or array of blocks.", "content.layout");

        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineInput(
            "layout",
            ArgumentType.Text,
            "DSL layout JSON",
            ui: new ArgumentUiMetadata
            {
                Group = "Layout",
                Order = 10,
                Multiline = true,
                Rows = 8,
                Format = "json",
            }
        );
        DefineInput("target", ArgumentType.Text, "Target path (e.g., 'main')", ui: new ArgumentUiMetadata { Group = "Placement", Order = 20 });
        DefineChoice("position", [("append", "Append"), ("prepend", "Prepend"), ("before", "Before"), ("after", "After"), ("replace", "Replace")], "Insert position", "append", ui: new ArgumentUiMetadata { Group = "Placement", Order = 30 });
        DefineInput(
            "block",
            ArgumentType.Text,
            "Block(s) to add - single object or array of blocks (DSL JSON)",
            ui: new ArgumentUiMetadata
            {
                Group = "Content",
                Order = 40,
                Multiline = true,
                Rows = 8,
                Format = "json",
            }
        );

        DefineOutput("layout", ArgumentType.Text, "Updated DSL layout JSON");

        DefineIcon(NodeIcon.Embedded("layout"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        string target = args.RequireText("target");
        string positionStr = args.GetText("position") ?? "append";

        // Get raw inputs - can be JsonElement or string
        object? layoutRaw = config.Resolve("layout");
        object? blockRaw = config.Resolve("block");

        services.PublishStatus($"Parsing layout (type: {layoutRaw?.GetType().Name ?? "null"})...");
        DslNode layout = DslSerializer.SafeDeserializeAny(layoutRaw, "layout");

        InsertPosition position = ParsePosition(positionStr);

        // Parse blocks - supports single or array
        services.PublishStatus($"Parsing block(s) (type: {blockRaw?.GetType().Name ?? "null"})...");
        List<DslNode> blocks = DslSerializer.SafeDeserializeFlexibleAny(blockRaw, "block");
        if (blocks.Count == 0)
        {
            throw new InvalidOperationException("No valid blocks found in 'block' input.");
        }

        services.PublishStatus($"Adding {blocks.Count} block(s) to '{target}' at position '{positionStr}'...");

        // When prepending multiple blocks, reverse order to maintain correct sequence
        // Example: [A, B] with prepend → prepend B first, then A → [A, B]
        List<DslNode> blocksToAdd = position == InsertPosition.Prepend ? blocks.AsEnumerable().Reverse().ToList() : blocks;

        DslNode updatedLayout = layout;
        foreach (DslNode block in blocksToAdd)
        {
            updatedLayout = DslManipulator.AddBlock(updatedLayout, target, block, position);
        }

        // Expand any embedded DSL in TextNode.Content
        updatedLayout = DslSerializer.ExpandEmbeddedDsl(updatedLayout);

        services.PublishStatus($"{blocks.Count} block(s) added successfully.");

        return ValueTask.FromResult(NodeResult.Create().AddText("layout", DslSerializer.Serialize(updatedLayout)));
    }

    private static InsertPosition ParsePosition(string value) =>
        value.ToLowerInvariant() switch
        {
            "append" => InsertPosition.Append,
            "prepend" => InsertPosition.Prepend,
            "before" => InsertPosition.Before,
            "after" => InsertPosition.After,
            "replace" => InsertPosition.Replace,
            _ => InsertPosition.Append,
        };
}
