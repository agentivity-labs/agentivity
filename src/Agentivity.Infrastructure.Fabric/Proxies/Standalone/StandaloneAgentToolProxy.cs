using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Resolves and routes the LLM-callable tools connected to an <see cref="AgentEntity"/>'s
/// <c>tools</c> port — the standalone equivalent of <see cref="McpToolRouter"/>, which reads
/// from <c>NodeConfig</c>/<c>WorkflowRunServices</c> (workflow-graph concepts the standalone
/// path does not have).
/// <para>
/// Mirrors <see cref="McpToolRouter.ListToolsAsync"/>/<see cref="McpToolRouter.CallToolAsync"/>
/// behaviour exactly (duplicate-name guard, "not connected" fallback result) so the standalone
/// path produces the same observable tool surface and call results as
/// <c>WorkflowAgentExecutionPreparationCoordinator</c> + <c>WorkflowAgentExecutionRuntimePort</c>
/// (characterized in <c>AgentEntityRunnerCharacterizationTests</c>).
/// </para>
/// </summary>
internal sealed class StandaloneAgentToolProxy
{
    private readonly IReadOnlyList<(InProcessMcpToolBridge Bridge, ResourceBinding Binding)> _bridges;

    private StandaloneAgentToolProxy(IReadOnlyList<(InProcessMcpToolBridge, ResourceBinding)> bridges)
    {
        _bridges = bridges;
    }

    /// <summary>
    /// Resolves all <see cref="IAgentTool"/> nodes connected to <paramref name="aiNodeId"/> via
    /// the ports that give an agent its tools (<see cref="AgentToolPorts"/>). Returns null when none are connected/recognized — callers should
    /// treat that the same as "no tools" (mirrors <c>McpToolRouter.ListToolsAsync</c> returning
    /// an empty list when <c>ResourceResolver.ResolveAll</c> finds nothing).
    /// <para><paramref name="runState"/> is the run's state (<c>RunContext.ToolState</c>), shared by its tools: what one keeps
    /// for the run (an In-Memory Vector Store), the others find — and it is saved with the run, so a resume finds it too. A
    /// workflow gives its own run state the same way.</para>
    /// </summary>
    public static StandaloneAgentToolProxy? TryCreate(AgentEntity agent, string aiNodeId, IDictionary<string, object?> runState)
    {
        var connections = agent.Graph.Connections.Where(c => string.Equals(c.To, aiNodeId, StringComparison.OrdinalIgnoreCase) && AgentToolPorts.All.Any(p => string.Equals(c.ToPort, p.Port, StringComparison.OrdinalIgnoreCase)));

        ArgumentNullException.ThrowIfNull(runState);
        var bridges = new List<(InProcessMcpToolBridge, ResourceBinding)>();
        foreach (var conn in connections)
        {
            var node = agent.Graph.FindNode(conn.From);
            if (node is null)
                continue;

            var tool = NodeInstanceRegistry.GetAs<IAgentTool>(node.NodeType);
            if (tool is null)
                continue;

            var resourceType = ResourceNodeRegistry.ResolveResourceType(node.NodeType) ?? ResourcePortCategory.AIAgentTool;
            // Carry the agent's graph so a tool node that itself needs a nested resource (e.g. an
            // AI Agent Data Table Tool's "data_table" port) can resolve it via
            // ResourceResolver.TryResolveNestedToolNode — that helper requires
            // ResourceBinding.Definition, which is otherwise null on the standalone-agent path.
            var binding = new ResourceBinding(conn.From, conn.FromPort, node, resourceType, runState, agent.Graph);
            bridges.Add((new InProcessMcpToolBridge(tool, binding), binding));
        }

        return bridges.Count > 0 ? new StandaloneAgentToolProxy(bridges) : null;
    }

    /// <summary>
    /// Lists every tool definition exposed by the connected resources — same duplicate-name
    /// guard as <see cref="McpToolRouter.ListToolsAsync"/> ("Tool names must be unique on the
    /// same agent.").
    /// </summary>
    public async ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<JsonObject>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (bridge, _) in _bridges)
        {
            foreach (var definition in await bridge.ListToolsAsync(cancellationToken).ConfigureAwait(false))
            {
                var toolName = LlmToolDefinitionJson.GetRequiredName(definition);

                if (!names.Add(toolName))
                    throw new InvalidOperationException($"Duplicate AI agent tool name detected: '{toolName}'. Tool names must be unique on the same agent.");

                result.Add((JsonObject)definition.DeepClone());
            }
        }

        return result;
    }

    /// <summary>
    /// Invokes the named tool on whichever connected resource exposes it — same "not connected"
    /// fallback result as <see cref="McpToolRouter.CallToolAsync"/> when no resource exposes it.
    /// </summary>
    public async ValueTask<JsonObject> CallToolAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        foreach (var (bridge, _) in _bridges)
        {
            var definitions = await bridge.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            if (!definitions.Any(definition => string.Equals(LlmToolDefinitionJson.GetRequiredName(definition), toolName, StringComparison.OrdinalIgnoreCase)))
                continue;

            return await bridge.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);
        }

        return McpToolResultJson.CreateTextResult($"Tool '{toolName}' is not connected to this AI agent.", true);
    }
}
