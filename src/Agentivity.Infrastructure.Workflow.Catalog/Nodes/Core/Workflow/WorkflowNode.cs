using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Workflow;

public sealed class WorkflowNode : ToolNode
{
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<WorkflowNode>? _logger;

    public WorkflowNode(IServiceProvider serviceProvider, ILogger<WorkflowNode> logger)
        : base("workflow.run")
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Run Workflow", "Executes a nested workflow with input parameters.", "core.workflow");

        // Inputs are resolved dynamically via config (workflow name, input, resultKey),
        // but we still surface a generic input port for tooling.
        DefineInput("workflow", ArgumentType.Text, "Nested workflow name", ui: new ArgumentUiMetadata { Group = "Workflow", Order = 10 });
        DefineInput(
            "input",
            ArgumentType.Text,
            "Nested workflow input payload",
            ui: new ArgumentUiMetadata
            {
                Group = "Workflow",
                Order = 20,
                Multiline = true,
                Rows = 8,
                Format = "json",
            }
        );
        DefineInput("resultKey", ArgumentType.Text, "Key under which to store nested workflow state", "", ui: new ArgumentUiMetadata { Group = "Options", Order = 30 });

        // Output: when resultKey is set, nested workflow state is stored under that key; otherwise merged.
        DefineOutput("state", ArgumentType.Text, "Nested workflow resulting state (direct or under resultKey)");
    }

    public WorkflowNode(string processName)
        : base("core.process")
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            throw new ArgumentException("Process name is required", nameof(processName));
        }

        DefineMetadata("Run Process", "Executes a named process workflow.", "core.workflow");

        // Inputs are resolved dynamically via config (workflow name, input, resultKey),
        // but we still surface a generic input port for tooling.
        DefineInput("workflow", ArgumentType.Text, "Nested workflow name", ui: new ArgumentUiMetadata { Group = "Workflow", Order = 10 });
        DefineInput(
            "input",
            ArgumentType.Text,
            "Nested workflow input payload",
            ui: new ArgumentUiMetadata
            {
                Group = "Workflow",
                Order = 20,
                Multiline = true,
                Rows = 8,
                Format = "json",
            }
        );
        DefineInput("resultKey", ArgumentType.Text, "Key under which to store nested workflow state", "", ui: new ArgumentUiMetadata { Group = "Options", Order = 30 });

        // Output: when resultKey is set, nested workflow state is stored under that key; otherwise merged.
        DefineOutput("result", ArgumentType.Text, "Result of the invoked process");

        DefineIcon(NodeIcon.Embedded("workflow"));
    }

    public override async ValueTask<NodeResult> ExecuteAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        if (_serviceProvider is null || _logger is null)
        {
            throw new InvalidOperationException("WorkflowNode requires IServiceProvider and ILogger to be injected.");
        }

        string? workflowName = config.Resolve("workflow")?.ToString();
        if (string.IsNullOrWhiteSpace(workflowName))
        {
            throw new InvalidOperationException($"Workflow node '{config.Node.Id}' requires 'workflow'.");
        }

        IDictionary<string, object?> input = ResolveInput(config.Resolve("input"));

        _logger.LogInformation("Executing nested workflow {Workflow} from node {Node}", workflowName, config.Node.Id);

        WorkflowEngine workflowEngine = _serviceProvider.GetRequiredService<WorkflowEngine>();
        OperationResult<WorkflowRunResult> result = await workflowEngine.ExecuteAsync(workflowName, input, cancellationToken).ConfigureAwait(false);
        return result.IsFailure || result.Value is null ? throw new InvalidOperationException(result.Error ?? $"Workflow '{workflowName}' failed to execute.") : BuildNodeResult(config, services, result.Value);
    }

    private static IDictionary<string, object?> ResolveInput(object? payload)
    {
        if (payload is null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        if (payload is string jsonString && !string.IsNullOrWhiteSpace(jsonString))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(jsonString) ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static NodeResult BuildNodeResult(NodeConfig config, WorkflowRunServices services, WorkflowRunResult runResult)
    {
        string? resultKey = config.Resolve("resultKey")?.ToString();
        string stateJson = System.Text.Json.JsonSerializer.Serialize(runResult.State);

        if (!string.IsNullOrWhiteSpace(resultKey))
        {
            return NodeResult.Create().AddText(resultKey!, stateJson);
        }

        // Convert state to NodeResult
        var result = NodeResult.Create();
        foreach ((string? key, object? value) in runResult.State)
        {
            _ = result.AddText(key, value?.ToString() ?? string.Empty);
        }
        return result;
    }
}
