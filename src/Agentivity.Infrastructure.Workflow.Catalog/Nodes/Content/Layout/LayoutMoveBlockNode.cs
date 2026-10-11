using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflow.Catalog.Domain.Layout;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Content.Layout;

/// <summary>
/// Moves a block from source to target position in the layout DSL.
/// This is a generic AST editor - no business logic.
/// </summary>
public sealed class LayoutMoveBlockNode : ToolNode
{
    public LayoutMoveBlockNode()
        : base("layout.move_block")
    {
        DefineMetadata("Move Block", "Moves a block from source to target position in the layout.", "content.layout");
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
        DefineInput("source", ArgumentType.Text, "Source path (e.g., 'main.block[1]')", ui: new ArgumentUiMetadata { Group = "Placement", Order = 20 }, required: true);
        DefineInput("target", ArgumentType.Text, "Target path (e.g., 'main')", ui: new ArgumentUiMetadata { Group = "Placement", Order = 30 }, required: true);
        DefineChoice("position", [("append", "Append"), ("prepend", "Prepend"), ("before", "Before"), ("after", "After")], "Insert position", "append", ui: new ArgumentUiMetadata { Group = "Placement", Order = 40 }, required: false);

        DefineOutput("layout", ArgumentType.Text, "Updated DSL layout JSON");

        DefineIcon(NodeIcon.Embedded("layout"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        string source = args.RequireText("source");
        string target = args.RequireText("target");
        string positionStr = args.GetText("position") ?? "append";

        // Get raw input - can be JsonElement or string
        object? layoutRaw = config.Resolve("layout");
        DslNode layout = DslSerializer.SafeDeserializeAny(layoutRaw, "layout");

        InsertPosition position = ParsePosition(positionStr);

        DslNode updatedLayout = DslManipulator.MoveBlock(layout, source, target, position);

        // Expand any embedded DSL in TextNode.Content
        updatedLayout = DslSerializer.ExpandEmbeddedDsl(updatedLayout);

        return ValueTask.FromResult(NodeResult.Create().AddText("layout", DslSerializer.Serialize(updatedLayout)));
    }

    private static InsertPosition ParsePosition(string value) =>
        value.ToLowerInvariant() switch
        {
            "append" => InsertPosition.Append,
            "prepend" => InsertPosition.Prepend,
            "before" => InsertPosition.Before,
            "after" => InsertPosition.After,
            _ => InsertPosition.Append,
        };
}
