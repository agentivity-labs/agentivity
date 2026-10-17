using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Display;

/// <summary>
/// Renders whatever is bound to <c>value</c> directly on the node — text, a number, or a JSON
/// object/array — so a workflow author can inspect a prior node's output on the canvas without
/// opening the run inspector. Purely a viewer: <c>value</c> passes straight through to <c>out</c>
/// unchanged, same shape it came in as.
/// </summary>
public sealed class ConsoleDisplayNode : DisplayNode
{
    public ConsoleDisplayNode()
        : base(
            "core.display.console",
            "Console",
            "Displays a value — text, numbers, or JSON — directly on the node.",
            "core.display",
            "To see a value (text, number or JSON) right on the canvas while building or checking a workflow. It passes the value on unchanged."
        )
    {
        DefineInput(
            "value",
            ArgumentType.Text,
            "The value to display. Bind it to any prior node's output, e.g. `{{ some_node.output }}`.",
            defaultValue: "",
            ui: new ArgumentUiMetadata { Group = "Display", Order = 20, Multiline = true, Rows = 3 },
            required: true
        );
        DefineOutput("value", ArgumentType.Text, "The same value, passed through unchanged.");

        DefineIcon(NodeIcon.Material(0xe86f, "#22c55e"));
    }

    protected override object? ResolveValue(NodeConfig config) => config.Resolve("value");

    protected override void WriteValueOutput(NodeResult result, object? value)
    {
        // Unresolved/unbound input — leave the output genuinely absent rather than serializing
        // C# null into the literal 4-character text "null", which would render as that word.
        if (value is null)
        {
            result.AddText("value", null);
            return;
        }

        if (value is string text)
        {
            result.AddText("value", text);
            return;
        }

        result.AddText("value", JsonSerializer.Serialize(value));
    }
}
