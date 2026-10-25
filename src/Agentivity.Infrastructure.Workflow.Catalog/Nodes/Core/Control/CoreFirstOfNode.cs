using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Control;

/// <summary>
/// FirstOf node that selects the first available value from multiple conditional branches.
/// Does not wait for all connections - executes as soon as one branch completes and traverses its connection.
/// Useful for cache-or-compute patterns (try cache first, fallback to computation).
/// </summary>
public sealed class CoreFirstOfNode : ToolNode, IPartialExecutionNode
{
    private readonly ILogger<CoreFirstOfNode> _logger;

    public CoreFirstOfNode(ILogger<CoreFirstOfNode> logger)
        : base("core.firstof")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("First Of", "Selects the first available value from multiple inputs. Does not wait for all connections.", "core.control", whenToUse: "To go on with whichever branch has run, when only one of several branches runs, for example a value read from storage or else computed. Not for choosing a path: use If or Switch instead.");
        DefineEffects(NodeEffects.None);

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Multiple input ports
        ClearInputPorts();
        DefineInputPort("input1", "Input 1", "First input", maxConnections: 1);
        DefineInputPort("input2", "Input 2", "Second input", maxConnections: 1);
        DefineInputPort("input3", "Input 3", "Third input (optional)", maxConnections: 1);
        DefineInputPort("input4", "Input 4", "Fourth input (optional)", maxConnections: 1);

        // Output port
        ClearOutputPorts();
        DefineOutputPort("out", "Out", "Selected output");

        // Configuration inputs
        DefineInput("mode", ArgumentType.Text, "Selection mode: 'passThrough', 'preferFirst', 'preferNonNull'", "passThrough", ui: new ArgumentUiMetadata { Group = "Settings", Order = 10 }, required: false);
        DefineInput("wait_for_all", ArgumentType.Boolean, "Wait for all connections before selecting", false, ui: new ArgumentUiMetadata { Group = "Settings", Order = 20 }, required: false);

        // Data inputs for manual values (if no connection)
        DefineInput(
            "value1",
            ArgumentType.Text,
            "Manual value for input1 (if not connected)",
            ui: new ArgumentUiMetadata
            {
                Group = "Inputs",
                Order = 30,
                Multiline = true,
                Rows = 3,
            },
            required: false
        );
        DefineInput(
            "value2",
            ArgumentType.Text,
            "Manual value for input2 (if not connected)",
            ui: new ArgumentUiMetadata
            {
                Group = "Inputs",
                Order = 40,
                Multiline = true,
                Rows = 3,
            },
            required: false
        );
        DefineInput(
            "value3",
            ArgumentType.Text,
            "Manual value for input3 (if not connected)",
            ui: new ArgumentUiMetadata
            {
                Group = "Inputs",
                Order = 50,
                Multiline = true,
                Rows = 3,
            },
            required: false
        );
        DefineInput(
            "value4",
            ArgumentType.Text,
            "Manual value for input4 (if not connected)",
            ui: new ArgumentUiMetadata
            {
                Group = "Inputs",
                Order = 60,
                Multiline = true,
                Rows = 3,
            },
            required: false
        );

        // Output
        DefineOutput("value", ArgumentType.Text, "Selected value");
        DefineOutput("source", ArgumentType.Text, "Which input provided the value");
        DefineOutput("inputs_received", ArgumentType.Number, "Number of inputs that were received");

        DefineIcon(NodeIcon.Embedded("core_firstof_node"));
    }

    public override ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        string mode = args.GetText("mode") ?? "passThrough";
        bool waitForAll = args.GetBoolean("wait_for_all") ?? false;

        // Get all available inputs - values can be any type (string, JsonElement, etc.)
        var inputs = new List<InputValue> { new(1, GetInputValue(args, "value1")), new(2, GetInputValue(args, "value2")), new(3, GetInputValue(args, "value3")), new(4, GetInputValue(args, "value4")) };

        var receivedInputs = inputs.Where(i => i.Value != null).ToList();

        _logger.LogInformation("FirstOf node '{NodeId}': received {Count} of {Total} inputs, mode={Mode}, waitForAll={WaitForAll}", config.Node.Id, receivedInputs.Count, inputs.Count, mode, waitForAll);

        // Check if we should wait for all inputs
        if (waitForAll && receivedInputs.Count < inputs.Count)
        {
            throw new InvalidOperationException($"FirstOf node '{config.Node.Id}': waiting for all inputs but only received {receivedInputs.Count} of {inputs.Count}");
        }

        // No inputs received
        if (receivedInputs.Count == 0)
        {
            services.PublishStatus("FirstOf: No inputs received");
            return ValueTask.FromResult(NodeResult.Create().AddText("value", null).AddText("source", null).AddNumber("inputs_received", 0));
        }

        // Select value based on mode
        InputValue? selectedInput = mode.ToLowerInvariant() switch
        {
            "passthrough" => receivedInputs.First(), // First input that arrived
            "preferfirst" => receivedInputs.OrderBy(i => i.Index).First(), // Lowest index
            "prefernotnull" => receivedInputs.FirstOrDefault(i => !IsNullOrEmpty(i.Value)),
            _ => receivedInputs.First(),
        };

        // Handle null selectedInput from prefernotnull mode
        if (selectedInput == null)
        {
            selectedInput = receivedInputs.First();
        }

        string valueType = selectedInput.Value?.GetType().Name ?? "null";
        services.PublishStatus($"FirstOf: Selected input{selectedInput.Index} (type={valueType}, mode={mode})");

        // Pass the value through as-is - structured data serialized to string
        NodeResult result = NodeResult.Create().AddText("source", $"input{selectedInput.Index}").AddNumber("inputs_received", receivedInputs.Count);

        // Add value based on its type — JsonElement must be serialized to string
        _ = selectedInput.Value is System.Text.Json.JsonElement jsonElement ? result.AddText("value", jsonElement.GetRawText()) : result.AddText("value", selectedInput.Value?.ToString());

        return ValueTask.FromResult(result);
    }

    private object? GetInputValue(ArgumentInputValues args, string key)
    {
        try
        {
            return args.GetText(key);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNullOrEmpty(object? value)
    {
        return value == null || (value is string s && string.IsNullOrWhiteSpace(s));
    }

    /// <summary>
    /// FirstOf executes as soon as ANY incoming connection has been traversed (at least one branch completed).
    /// This allows it to work with conditional branches where only one branch executes.
    /// </summary>
    /// <param name="traversedConnectionsCount">Number of incoming connections that have been traversed</param>
    /// <param name="totalConnectionsCount">Total number of incoming connections to this node</param>
    /// <returns>True to execute now, false to wait for more connections</returns>
    public bool ShouldExecuteWithTraversedConnections(int traversedConnectionsCount, int totalConnectionsCount)
    {
        // Execute immediately if at least one incoming connection has been traversed
        // Don't wait for connections from branches that will never execute
        _logger.LogDebug("FirstOf: {Traversed}/{Total} connections traversed - {Decision}", traversedConnectionsCount, totalConnectionsCount, traversedConnectionsCount > 0 ? "READY" : "WAITING");

        return traversedConnectionsCount > 0;
    }

    private record InputValue(int Index, object? Value);
}
