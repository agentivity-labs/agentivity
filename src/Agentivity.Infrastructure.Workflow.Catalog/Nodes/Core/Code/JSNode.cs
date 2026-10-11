using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.CodeExecution;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Code;

/// <summary>
/// Runs a JavaScript script (a real Node.js process, sandboxed — see
/// <c>Agentivity.Module.CodeExecution</c>) and returns its result. Deliberately tiny: the only
/// thing specific to JavaScript here is the <c>code</c> parameter declaration — everything else
/// (resolving the timeout, calling the runner, wrapping the result) is <see cref="CodeNode"/>.
/// </summary>
public sealed class JSNode : CodeNode
{
    public JSNode(ICodeRunner runner)
        : base(
            "core.code.javascript",
            "JavaScript",
            "Runs a JavaScript script (a real, sandboxed Node.js process) and returns its result.",
            runner
        )
    {
        DefineInput(
            "code",
            ArgumentType.Text,
            "JavaScript source. Reference a prior node's output directly, e.g. `{{ some_node.field }}` "
                + "— it's substituted into the script before it runs. The last evaluated expression "
                + "becomes `output` — no `return` needed unless you wrap your own function.",
            defaultValue: "1 + 1;",
            ui: new ArgumentUiMetadata
            {
                Group = "Script",
                Order = 10,
                Multiline = true,
                Rows = 12,
            },
            required: false
        );

        DefineIcon(NodeIcon.Material(0xe86f, "#f7df1e"));
    }

    protected override string CodeParameterName => "code";
}
