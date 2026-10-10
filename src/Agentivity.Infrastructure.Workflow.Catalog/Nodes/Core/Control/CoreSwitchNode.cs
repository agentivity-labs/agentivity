using System.Text.Json;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Core.Control;

/// <summary>
/// Switch node that evaluates a value and routes execution to different branches based on cases.
/// Similar to switch/case in programming languages.
/// </summary>
public sealed class CoreSwitchNode : ControlFlowNode
{
    private readonly ILogger<CoreSwitchNode> _logger;

    public CoreSwitchNode(ILogger<CoreSwitchNode> logger)
        : base("core.switch")
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        DefineMetadata("Switch", "Evaluates a value and routes execution to different branches based on matching cases.", "core.control");

        ConfigureMetadata();
    }

    private void ConfigureMetadata()
    {
        // Input port
        ClearInputPorts();
        DefineInputPort("in", "In", "Entry point", maxConnections: 1);

        // Data inputs
        DefineInput("value", ArgumentType.Text, "Value to evaluate", ui: new ArgumentUiMetadata { Group = "Switch", Order = 10 });
        DefineInput("mode", ArgumentType.Text, "Matching mode: 'exact', 'contains', 'regex'", "exact", ui: new ArgumentUiMetadata { Group = "Switch", Order = 20 });
        DefineInput("default_branch", ArgumentType.Text, "Default branch if no case matches", "default", ui: new ArgumentUiMetadata { Group = "Options", Order = 30 });

        // Note: Output ports are dynamically defined based on cases in metadata
        // Cases should be defined in metadata.cases as: [{ "value": "case1", "port": "branch1", "label": "Branch 1" }]

        DefineOutput("matched_case", ArgumentType.Text, "The case value that matched");
        DefineOutput("branch_taken", ArgumentType.Text, "The branch port that was taken");

        DefineIcon(NodeIcon.Embedded("core_switch_node"));
    }

    public override ValueTask<ControlFlowResult> EvaluateAsync(NodeConfig config, WorkflowRunServices services, CancellationToken cancellationToken = default)
    {
        ArgumentInputValues args = GetInputs(config);

        string value = args.GetText("value") ?? string.Empty;
        string mode = args.GetText("mode") ?? "exact";
        string defaultBranch = args.GetText("default_branch") ?? "default";

        // Get cases from metadata
        List<SwitchCase> cases = GetCasesFromMetadata(config, services);

        if (cases.Count == 0)
        {
            _logger.LogWarning("Switch node '{NodeId}': No cases defined, using default branch", config.Node.Id);
            return TakeBranch(config, services, defaultBranch, null, "No cases defined");
        }

        // Find matching case
        SwitchCase? matchedCase = FindMatchingCase(value, cases, mode);

        if (matchedCase != null)
        {
            _logger.LogInformation("Switch node '{NodeId}': value='{Value}' matched case '{MatchedCase}', taking branch '{Branch}'", config.Node.Id, value, matchedCase.CaseValue, matchedCase.Port);

            return TakeBranch(config, services, matchedCase.Port, matchedCase.CaseValue, $"Matched: {matchedCase.Label}");
        }

        // No match found, use default
        _logger.LogInformation("Switch node '{NodeId}': value='{Value}' did not match any case, taking default branch '{Branch}'", config.Node.Id, value, defaultBranch);

        return TakeBranch(config, services, defaultBranch, null, "Default (no match)");
    }

    private ValueTask<ControlFlowResult> TakeBranch(NodeConfig config, WorkflowRunServices services, string port, string? matchedCase, string reason)
    {
        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["matched_case"] = matchedCase, ["branch_taken"] = port };

        services.PublishStatus($"Switch: {reason} → {port}");

        return ValueTask.FromResult(ControlFlowResult.Branch(port, outputs));
    }

    private List<SwitchCase> GetCasesFromMetadata(NodeConfig config, WorkflowRunServices services)
    {
        var cases = new List<SwitchCase>();

        if (!config.Node.Metadata.TryGetValue("cases", out object? rawCases) || rawCases is null)
        {
            return cases;
        }

        if (rawCases is not JsonElement json || json.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Switch node '{NodeId}': metadata.cases is not a JSON array", config.Node.Id);
            return cases;
        }

        foreach (JsonElement item in json.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string caseValue = item.TryGetProperty("value", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

            string port = item.TryGetProperty("port", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

            string label = item.TryGetProperty("label", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? port : port;

            if (!string.IsNullOrEmpty(caseValue) && !string.IsNullOrEmpty(port))
            {
                cases.Add(new SwitchCase(caseValue, port, label));
            }
        }

        return cases;
    }

    private SwitchCase? FindMatchingCase(string value, List<SwitchCase> cases, string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "exact" => cases.FirstOrDefault(c => string.Equals(c.CaseValue, value, StringComparison.OrdinalIgnoreCase)),
            "contains" => cases.FirstOrDefault(c => value.Contains(c.CaseValue, StringComparison.OrdinalIgnoreCase)),
            "regex" => FindRegexMatch(value, cases),
            _ => cases.FirstOrDefault(c => string.Equals(c.CaseValue, value, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private SwitchCase? FindRegexMatch(string value, List<SwitchCase> cases)
    {
        foreach (SwitchCase switchCase in cases)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(switchCase.CaseValue);
                if (regex.IsMatch(value))
                {
                    return switchCase;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", switchCase.CaseValue);
            }
        }

        return null;
    }

    private record SwitchCase(string CaseValue, string Port, string Label);
}
