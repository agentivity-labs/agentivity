using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.Layout;

/// <summary>
/// Replaces a block at the specified path with a new block.
/// This is a generic AST editor - no business logic.
/// </summary>
public sealed class LayoutReplaceBlockNode : ToolNode
{
    public LayoutReplaceBlockNode()
        : base("layout.replace_block")
    {
        DefineMetadata("Replace Block", "Replaces a block at the specified path with one or more new blocks.", "content.layout", whenToUse: "To swap one block of a page layout in DSL format, named by its path, for one or more new blocks given as JSON. To keep the block and add next to it, use Add Block instead.");
        DefineEffects(NodeEffects.None);

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
            },
            required: true
        );
        DefineInput("target", ArgumentType.Text, "Target path to replace (e.g., 'root.main.block[1]')", ui: new ArgumentUiMetadata { Group = "Placement", Order = 20 }, required: true);
        DefineInput(
            "block",
            ArgumentType.Text,
            "New block(s) - single object or array of blocks (DSL JSON)",
            ui: new ArgumentUiMetadata
            {
                Group = "Content",
                Order = 30,
                Multiline = true,
                Rows = 8,
                Format = "json",
            },
            required: true
        );

        DefineOutput("layout", ArgumentType.Text, "Updated DSL layout JSON");

        DefineIcon(NodeIcon.Embedded("layout"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        string target = args.RequireText("target");

        // Get raw inputs - can be JsonElement or string
        object? layoutRaw = config.Resolve("layout");
        object? blockRaw = config.Resolve("block");

        DslNode layout = DslSerializer.SafeDeserializeAny(layoutRaw, "layout");
        List<DslNode> blocks = DslSerializer.SafeDeserializeFlexibleAny(blockRaw, "block");

        if (blocks.Count == 0)
        {
            throw new InvalidOperationException("No valid blocks found in 'block' input.");
        }

        // Replace with first block, then add remaining blocks after it
        DslNode updatedLayout = DslManipulator.ReplaceBlock(layout, target, blocks[0]);

        // If multiple blocks, add the rest after the replaced one
        if (blocks.Count > 1)
        {
            for (int i = 1; i < blocks.Count; i++)
            {
                updatedLayout = DslManipulator.AddBlock(updatedLayout, target, blocks[i], InsertPosition.After);
            }
        }

        // Expand any embedded DSL in TextNode.Content
        updatedLayout = DslSerializer.ExpandEmbeddedDsl(updatedLayout);

        return ValueTask.FromResult(NodeResult.Create().AddText("layout", DslSerializer.Serialize(updatedLayout)));
    }
}
