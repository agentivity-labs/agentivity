using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Tools.Utils;

/// <summary>
/// AI-agent callable date/time tool.
/// Useful for grounding an agent in the current date or time without leaking other tool families.
/// </summary>
public sealed class AIAgentDateTimeToolNode : ResourceNode, IAgentTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string ToolName = "current_datetime";

    private const string InputSchema = """
{
  "type": "object",
  "properties": {
    "time_zone": {
      "type": "string",
      "enum": ["utc", "local"],
      "description": "Whether the tool should return UTC or local time"
    },
    "format": {
      "type": "string",
      "description": "Optional .NET date/time format string for the formatted value"
    }
  },
  "additionalProperties": false
}
""";

    public AIAgentDateTimeToolNode()
        : base("ai.tools.utils.datetime")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentTool);

        DefineMetadata("AI Agent Date & Time Tool", "Returns the current date/time in UTC or local time for AI agent tool calling.", "ai.tools.utils");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Date & Time Tool", ResourcePortCategory.AIAgentTool, maxConnections: -1);

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<JsonObject>>([LlmToolDefinitionJson.Create(ToolName, "Get the current date and time in UTC or local time.", InputSchema)]);
    }

    public ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (!string.Equals(ToolName, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(McpToolResultJson.CreateTextResult($"Tool '{toolName}' is not exposed by this node.", true));
        }

        string? timeZone = arguments.TryGetProperty("time_zone", out JsonElement timeZoneElement) ? timeZoneElement.GetString() : "utc";

        string? format = arguments.TryGetProperty("format", out JsonElement formatElement) ? formatElement.GetString() : null;

        DateTimeOffset timestamp = string.Equals(timeZone, "local", StringComparison.OrdinalIgnoreCase) ? DateTimeOffset.Now : DateTimeOffset.UtcNow;

        var payload = new
        {
            time_zone = string.Equals(timeZone, "local", StringComparison.OrdinalIgnoreCase) ? "local" : "utc",
            iso_8601 = timestamp.ToString("O", CultureInfo.InvariantCulture),
            formatted = timestamp.ToString(string.IsNullOrWhiteSpace(format) ? "F" : format, CultureInfo.InvariantCulture),
            unix_time_seconds = timestamp.ToUnixTimeSeconds(),
        };

        return ValueTask.FromResult(McpToolResultJson.CreateTextResult(JsonSerializer.Serialize(payload, SerializerOptions), false));
    }
}
