using Agentivity.Domain.Workflow;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.CodeExecution;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools;

/// <summary>
/// Lets an AI agent run a JavaScript snippet (real Node.js, sandboxed — see
/// <c>Agentivity.Module.CodeExecution</c>) as a callable tool, for calculations or data
/// transforms an LLM shouldn't attempt via free-form text. Deliberately a separate node from
/// <c>JSNode</c> (the workflow-node adapter over the same <see cref="ICodeRunner"/>) rather than
/// making <c>JSNode</c> carry both the "workflow node" and "agent tool" roles — the resource port
/// category (<see cref="ResourcePortCategory.AIAgentTool"/> vs a plain executable node) and the
/// error-handling contract (<see cref="IAgentTool"/>'s recoverable-vs-unrecoverable split vs a
/// thrown exception failing the whole workflow node) are different enough to want their own thin
/// wrapper. <see cref="ICodeRunner"/> is resolved via DI like any other dependency — if
/// <c>Agentivity.Module.CodeExecution</c> isn't loaded/enabled, constructing this node fails with
/// a standard DI error, not a silent no-op.
/// </summary>
public sealed class CodeToolNode : ResourceNode, IAgentTool
{
    private const string ToolName = "run_javascript";
    private const double DefaultTimeoutSeconds = 5;
    private const double MaxTimeoutSeconds = 30;

    private const string InputSchema = """
{
  "type": "object",
  "properties": {
    "code": {
      "type": "string",
      "description": "JavaScript source to run. The last evaluated top-level expression becomes the result; use an IIFE with an explicit return for early-return control flow."
    },
    "input": {
      "type": "object",
      "description": "Optional JSON object passed into the script as the `input` global."
    }
  },
  "required": ["code"],
  "additionalProperties": false
}
""";

    private readonly ICodeRunner _runner;

    public CodeToolNode(ICodeRunner runner)
        : base("ai.tools.code_javascript")
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("AI Agent JavaScript Tool", "Lets an AI agent run a JavaScript snippet (real, sandboxed Node.js) and use the result.", "ai.tools", whenToUse: "To let an agent write and run a small piece of JavaScript for exact calculations or reshaping data (sorting, filtering, converting a list). The code cannot reach files or the internet: to read a web page, use AI Agent Web Scraper Tool (Direct) instead.");
        DefineEffects(NodeEffects.None);

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent JavaScript Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);
        DefineInput(
            "timeout_seconds",
            ArgumentType.Number,
            "Execution timeout per call, in seconds.",
            defaultValue: DefaultTimeoutSeconds,
            ui: new ArgumentUiMetadata { Group = "Execution", Order = 10, Min = 0.1, Max = MaxTimeoutSeconds },
            required: false
        );

        DefineIcon(NodeIcon.Material(0xe86f, "#f7df1e"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<JsonObject>>(
            [LlmToolDefinitionJson.Create(ToolName, "Run a JavaScript snippet (sandboxed Node.js, no filesystem/network access) and return its result. Use for calculations, data transforms, or logic that shouldn't be attempted via free-form text.", InputSchema)]
        );
    }

    public async ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return McpToolResultJson.CreateFailedResult($"Tool '{toolName}' is not exposed by this node.", "unknown_tool");
        }

        if (!arguments.TryGetProperty("code", out JsonElement codeElement) || codeElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(codeElement.GetString()))
        {
            return McpToolResultJson.CreateFailedResult("The 'code' argument is required and must be a non-empty string.", "missing_code");
        }

        string code = codeElement.GetString()!;
        object? input = arguments.TryGetProperty("input", out JsonElement inputElement) && inputElement.ValueKind != JsonValueKind.Undefined ? inputElement : null;

        double requestedTimeout = binding.GetDouble("timeout_seconds") ?? DefaultTimeoutSeconds;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(requestedTimeout, 0.1, MaxTimeoutSeconds));

        try
        {
            CodeExecutionResult result = await _runner.ExecuteAsync(code, input, timeout, cancellationToken).ConfigureAwait(false);
            var payload = new JsonObject { ["output"] = JsonSerializer.SerializeToNode(result.Output), ["logs"] = new JsonArray([.. result.Logs.Select(l => JsonValue.Create(l))]) };
            return McpToolResultJson.CreateTextResult(payload.ToJsonString(), isError: false);
        }
        catch (CodeExecutionException ex)
        {
            // The script itself threw/failed — recoverable: the agent can rewrite the code (fix a
            // syntax error, handle an edge case) and try again.
            return McpToolResultJson.CreateFailedResult($"The script failed: {ex.Message}", "script_error");
        }
        catch (TimeoutException ex)
        {
            return McpToolResultJson.CreateFailedResult(ex.Message, "timeout");
        }
        // Any other exception (worker crash, infrastructure failure) is not something rewriting
        // the code can fix — let it propagate and stop the agent, per IAgentTool's contract.
    }
}
