using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.Layout;

/// <summary>
/// Removes a block from the layout DSL at the specified path.
/// This is a generic AST editor - no business logic.
/// </summary>
public sealed class LayoutRemoveBlockNode : ToolNode
{
    public LayoutRemoveBlockNode()
        : base("layout.remove_block")
    {
        DefineMetadata("Remove Block", "Removes a block from the layout at the specified path.", "content.layout");
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
        DefineInput("target", ArgumentType.Text, "Target path to remove (e.g., 'root.main.section[2]')", ui: new ArgumentUiMetadata { Group = "Placement", Order = 20 }, required: true);

        DefineOutput("layout", ArgumentType.Text, "Updated DSL layout JSON");

        DefineIcon(NodeIcon.Embedded("layout"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        string target = args.RequireText("target");

        // Get raw input - can be JsonElement or string
        object? layoutRaw = config.Resolve("layout");
        DslNode layout = DslSerializer.SafeDeserializeAny(layoutRaw, "layout");

        DslNode updatedLayout = DslManipulator.RemoveBlock(layout, target);

        // Expand any embedded DSL in TextNode.Content
        updatedLayout = DslSerializer.ExpandEmbeddedDsl(updatedLayout);

        return ValueTask.FromResult(NodeResult.Create().AddText("layout", DslSerializer.Serialize(updatedLayout)));
    }
}
