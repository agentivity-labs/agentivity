using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Conditions;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Control;

/// <summary>
/// Control flow node that evaluates a condition and branches execution.
/// </summary>
public sealed class CoreIfNode : ControlFlowNode
{
    private readonly ILogger<CoreIfNode> _logger;

    public CoreIfNode(ILogger<CoreIfNode> logger)
        : base("core.if")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("If", "Evaluates a condition and branches execution to either the true or false path.", "core.control", whenToUse: "To take one of two paths depending on a condition, for example whether a value is empty or above a threshold. For more than two paths, use Switch instead.");
        DefineEffects(NodeEffects.None);

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Input port
        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);

        // Output ports: true and false branches
        ClearOutputPorts();
        DefineOutputPort("true", "True", "Execute this path if condition is true");
        DefineOutputPort("false", "False", "Execute this path if condition is false");

        // Data inputs
        DefineInput(
            "conditions",
            ArgumentType.Structured,
            "Structured conditions (field, operator, value rows) — used instead of `condition` when it has rows. Build it with the condition builder instead of writing an expression.",
            defaultValue: "{\"combinator\":\"all\",\"rows\":[]}",
            ui: new ArgumentUiMetadata { Editor = "condition_group", Group = "Condition", Order = 5 },
            required: false
        );
        DefineInput("condition", ArgumentType.Boolean, "The condition to evaluate. Ignored when `conditions` has rows.", ui: new ArgumentUiMetadata { Group = "Condition", Order = 10 }, required: false);
        DefineInput("true_branch_label", ArgumentType.Text, "Optional label for the true branch (for debugging)", ui: new ArgumentUiMetadata { Group = "Options", Order = 20 }, required: false);
        DefineInput("false_branch_label", ArgumentType.Text, "Optional label for the false branch (for debugging)", ui: new ArgumentUiMetadata { Group = "Options", Order = 30 }, required: false);

        // Data outputs
        DefineOutput("result", ArgumentType.Boolean, "The evaluated condition result");
        DefineOutput("branch_taken", ArgumentType.Text, "The branch that was taken (true or false)");

        DefineIcon(NodeIcon.Embedded("core_if_node"));
    }

    public override ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        // Structured conditions take priority when they have rows; otherwise fall back to the
        // legacy scalar `condition` field, unchanged — keeps existing saved workflows working.
        ConditionGroup conditionGroup = ConditionGroup.Parse(args.GetJsonElement("conditions"));
        bool condition = conditionGroup.Rows.Count > 0 ? ConditionEvaluator.Evaluate(conditionGroup) : ConditionValueCoercion.ToBoolean(args.GetRaw("condition"));

        // Get optional labels for logging
        string trueBranchLabel = args.GetText("true_branch_label") ?? "True";
        string falseBranchLabel = args.GetText("false_branch_label") ?? "False";

        // Determine which branch to take
        string branchPort = condition ? "true" : "false";
        string branchLabel = condition ? trueBranchLabel : falseBranchLabel;

        _logger.LogInformation("If node '{NodeId}': condition={Condition}, taking '{BranchPort}' branch ({BranchLabel})", config.Node.Id, condition, branchPort, branchLabel);

        // Create outputs
        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["result"] = condition, ["branch_taken"] = branchPort };

        // Return branch control flow result
        return ValueTask.FromResult(ControlFlowResult.Branch(branchPort, outputs));
    }

}
