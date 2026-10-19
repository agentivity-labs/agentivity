using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Workflows.Resources;

public sealed class InProcessMcpToolBridge
{
    private readonly IAgentTool _tool;
    private readonly ResourceBinding _binding;

    public InProcessMcpToolBridge(IAgentTool tool, ResourceBinding binding)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(CancellationToken cancellationToken = default) => _tool.ListToolsAsync(_binding, cancellationToken);

    public ValueTask<JsonObject> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) => _tool.CallToolAsync(_binding, toolName, arguments, cancellationToken);
}

public static class McpToolResultJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static JsonObject CreateTextResult(string content, bool isError, DebugTraceItem? debug = null)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = content },
            },
            ["isError"] = isError,
        };

        if (debug is not null)
        {
            result["debug"] = JsonSerializer.SerializeToNode(debug, SerializerOptions);
        }

        return result;
    }

    public static string GetTextContent(JsonObject callResult)
    {
        ArgumentNullException.ThrowIfNull(callResult);

        if (!callResult.TryGetPropertyValue("content", out var contentNode) || contentNode is not JsonArray contentItems)
        {
            return callResult.ToJsonString();
        }

        var parts = new List<string>(contentItems.Count);

        foreach (var itemNode in contentItems)
        {
            if (itemNode is not JsonObject item)
            {
                continue;
            }

            if (!item.TryGetPropertyValue("type", out var typeNode) || typeNode is null || typeNode.GetValueKind() != JsonValueKind.String || !string.Equals(typeNode.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetPropertyValue("text", out var textNode) || textNode is null)
            {
                continue;
            }

            parts.Add(textNode.GetValueKind() == JsonValueKind.String ? textNode.GetValue<string>() : textNode.ToJsonString());
        }

        return parts.Count == 0 ? string.Empty : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    public static bool IsError(JsonObject callResult)
    {
        ArgumentNullException.ThrowIfNull(callResult);

        return callResult.TryGetPropertyValue("isError", out var isErrorNode) && isErrorNode is not null && isErrorNode.GetValueKind() is JsonValueKind.True or JsonValueKind.False && isErrorNode.GetValue<bool>();
    }

    public static DebugTraceItem? GetDebug(JsonObject callResult)
    {
        ArgumentNullException.ThrowIfNull(callResult);

        return callResult.TryGetPropertyValue("debug", out var debugNode) && debugNode is not null ? debugNode.Deserialize<DebugTraceItem>(SerializerOptions) : null;
    }

    /// <summary>
    /// Creates a recoverable tool error result with an explicit machine-readable reason.
    /// Use this when the tool can explain *why* it failed and the join LLM might adapt
    /// (e.g. retry with different parameters, choose an alternative approach).
    /// Keep <paramref name="failedReason"/> short, lowercase, underscore-separated
    /// (e.g. "invalid_expression", "rate_limit", "missing_required_field").
    /// For failures that no replanning can fix, throw from <c>CallToolAsync</c> instead.
    /// </summary>
    public static JsonObject CreateFailedResult(string message, string failedReason, DebugTraceItem? debug = null)
    {
        var result = CreateTextResult(message, isError: true, debug);
        result["failed_reason"] = failedReason;
        return result;
    }

    /// <summary>
    /// Returns the <c>failed_reason</c> set by <see cref="CreateFailedResult"/>, or <c>null</c>
    /// if the result was created without an explicit reason.
    /// </summary>
    public static string? GetFailedReason(JsonObject callResult)
    {
        ArgumentNullException.ThrowIfNull(callResult);
        return callResult.TryGetPropertyValue("failed_reason", out var node) && node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }
}

public static class McpToolRouter
{
    private const string BridgeCacheStateKey = "__mcp_tool_bridges";

    public static ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(NodeConfig context, WorkflowRunServices? services, string portName, string resourceType, CancellationToken cancellationToken = default) =>
        ListToolsAsync(context, services, [(portName, resourceType)], cancellationToken);

    /// <summary>The tools of every resource on <paramref name="ports"/> (an agent's: <see cref="AgentToolPorts.All"/>); a name given twice is refused.</summary>
    public static async ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(NodeConfig context, WorkflowRunServices? services, IReadOnlyList<(string Port, string ResourceType)> ports, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ports);

        var bridges = ports.SelectMany(p => ResolveBridges(context, services, p.Port, p.ResourceType, cancellationToken)).ToList();
        var result = new List<JsonObject>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bridge in bridges)
        {
            foreach (var definition in await bridge.ListToolsAsync(cancellationToken).ConfigureAwait(false))
            {
                var toolName = LlmToolDefinitionJson.GetRequiredName(definition);

                if (!names.Add(toolName))
                {
                    throw new InvalidOperationException($"Duplicate AI agent tool name detected: '{toolName}'. Tool names must be unique on the same agent.");
                }

                result.Add((JsonObject)definition.DeepClone());
            }
        }

        return result;
    }

    public static ValueTask<JsonObject> CallToolAsync(NodeConfig context, string portName, string resourceType, string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        CallToolAsync(context, [(portName, resourceType)], toolName, arguments, cancellationToken);

    /// <summary>Calls the tool of that name among the resources on <paramref name="ports"/>.</summary>
    public static async ValueTask<JsonObject> CallToolAsync(NodeConfig context, IReadOnlyList<(string Port, string ResourceType)> ports, string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var bridges = ports.SelectMany(p => ResolveBridges(context, null, p.Port, p.ResourceType, cancellationToken)).ToList();

        foreach (var bridge in bridges)
        {
            var definitions = await bridge.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            if (!definitions.Any(definition => string.Equals(LlmToolDefinitionJson.GetRequiredName(definition), toolName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return await bridge.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        }

        return McpToolResultJson.CreateTextResult($"Tool '{toolName}' is not connected to this AI agent.", true);
    }

    private static IReadOnlyList<InProcessMcpToolBridge> ResolveBridges(NodeConfig context, WorkflowRunServices? services, string portName, string resourceType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bindings = ResourceResolver.ResolveAll(context, portName, resourceType);
        if (bindings.Count == 0)
        {
            return [];
        }

        var bridges = new List<InProcessMcpToolBridge>(bindings.Count);

        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // On a port that is not only for tools (knowledge), what gives no tools is left to its own use.
            if (resourceType != ResourcePortCategory.AIAgentTool && NodeInstanceRegistry.GetPrototype(binding.SourceNode.NodeType) is not IAgentTool)
                continue;

            bridges.Add(GetOrCreateBridge(context, services, binding, portName));
        }

        return bridges;
    }

    private static InProcessMcpToolBridge GetOrCreateBridge(NodeConfig context, WorkflowRunServices? services, ResourceBinding binding, string portName)
    {
        if (!context.State.TryGetValue(BridgeCacheStateKey, out var cacheObject) || cacheObject is not Dictionary<string, InProcessMcpToolBridge> cache)
        {
            cache = new Dictionary<string, InProcessMcpToolBridge>(StringComparer.OrdinalIgnoreCase);
            context.State[BridgeCacheStateKey] = cache;
        }

        var cacheKey = $"{binding.SourceNodeId}:{binding.SourcePort}:{binding.ResourceType}";
        if (cache.TryGetValue(cacheKey, out var existingBridge))
        {
            return existingBridge;
        }

        var tool = NodeInstanceRegistry.GetAs<IAgentTool>(binding.SourceNode.NodeType) ?? throw new InvalidOperationException($"Node instance '{binding.SourceNode.NodeType}' is not registered.");

        if (tool is null)
        {
            throw new InvalidOperationException($"Node '{binding.SourceNode.NodeType}' connected on port '{portName}' does not expose MCP tools.");
        }

        var bridge = new InProcessMcpToolBridge(tool, binding);

        cache[cacheKey] = bridge;
        return bridge;
    }
}
