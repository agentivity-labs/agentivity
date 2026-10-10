using System.Text.Json;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.CodeExecution;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Code;

/// <summary>
/// Shared plumbing for any "run a script" node — resolves the common <c>timeoutSeconds</c>
/// parameter, calls an injected <see cref="ICodeRunner"/>, and wraps the JSON result into the
/// standard <see cref="NodeResult"/> shape. Deliberately thin: no process management, no
/// sandboxing decisions — that all lives in whichever module actually registers the
/// <see cref="ICodeRunner"/> this node calls (e.g. <c>Agentivity.Module.CodeExecution</c> for
/// JavaScript). This class has no reference to, and no knowledge of, how the code actually runs —
/// a future language node (e.g. Python) extends this the same way <see cref="JSNode"/> does,
/// supplying its own <see cref="ICodeRunner"/> and its own language-specific parameter(s).
/// <para>
/// No generic <c>input</c> parameter: a workflow author references a prior node's output
/// directly inside the script text via <c>{{ node_id.field }}</c> — the same template resolver
/// every other text field goes through — which the engine substitutes before the script ever
/// runs. Simpler than threading values through a second indirection (an <c>input</c> object the
/// script would then have to destructure), and each reference stays visible right where it's
/// used in the code.
/// </para>
/// </summary>
public abstract class CodeNode : ToolNode
{
    /// <summary>
    /// Hard ceiling on the timeout a node's <c>timeoutSeconds</c> config can request — that field
    /// is a UX convenience, not a security boundary; a workflow author typing a huge number
    /// (or leaving a stale huge default from a copy-pasted node) must never bypass this.
    /// </summary>
    protected const double MaxTimeoutSeconds = 30;

    private readonly ICodeRunner _runner;

    protected CodeNode(string name, string displayName, string description, ICodeRunner runner) : base(name)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

        DefineMetadata(displayName, description, "core.code");
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);
        DefineOutputPort("out", "Out", "Default output port");

        DefineInput(
            "timeoutSeconds",
            ArgumentType.Number,
            "Execution timeout in seconds.",
            defaultValue: 5.0,
            ui: new ArgumentUiMetadata { Group = "Execution", Order = 90 }
        );

    }

    /// <summary>The name of the parameter holding this node's script source (e.g. <see cref="JSNode"/>'s <c>code</c>).</summary>
    protected abstract string CodeParameterName { get; }

    /// <summary>
    /// No fixed output schema: the script's own returned object shape *is* the node's output
    /// shape. Each top-level key of what the script returns becomes its own <see cref="NodeResult"/>
    /// entry directly — e.g. `({ subtotal, tax, total })` produces exactly `subtotal`/`tax`/`total`,
    /// nothing else. Lets a workflow author bind `{{ this_node.total }}` straight to the value, and
    /// see each one separately in the run inspector's Outputs tab, which reflects this dictionary
    /// verbatim at runtime (no pre-declared schema involved). A script returning a non-object
    /// (a scalar, an array, or nothing) produces no output fields — there is nothing to name.
    /// </summary>
    public sealed override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        var code = config.Resolve(CodeParameterName) as string ?? string.Empty;
        var requestedTimeout = args.GetDouble("timeoutSeconds") ?? 5.0;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(requestedTimeout, 0.1, MaxTimeoutSeconds));

        CodeExecutionResult result = await _runner.ExecuteAsync(code, input: null, timeout, cancellationToken).ConfigureAwait(false);

        var nodeResult = NodeResult.Create();
        if (result.Output is JsonElement { ValueKind: JsonValueKind.Object } outputObject)
        {
            foreach (var property in outputObject.EnumerateObject())
                nodeResult[property.Name] = FlattenJsonElement(property.Value);
        }

        return nodeResult;
    }

    /// <summary>Converts a JSON value into one of the primitive types <see cref="NodeResult"/> accepts — nested objects/arrays fall back to their raw JSON text, same convention as the whole <c>output</c> field.</summary>
    private static object? FlattenJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
}
