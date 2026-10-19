using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Workflow implementation of <see cref="IAgentToolsProxy"/>.
/// Delegates to <see cref="McpToolRouter"/> for connected MCP/AI-agent tools. AG-UI custom
/// widgets are not a tool-catalog concern — they're declared on the chat channel and reach the
/// LLM as <c>interact_with_human</c> content-block options (see <c>ChatChannel.ResolveInteractionWidgets</c>),
/// which already applies uniformly to every entity kind.
/// </summary>
public sealed class WorkflowToolsProxy(NodeConfig config, WorkflowRunServices services) : IAgentToolsProxy
{
    public async ValueTask<IReadOnlyList<JsonObject>> ListConnectedToolsAsync(CancellationToken ct) => await McpToolRouter.ListToolsAsync(config, services, AgentToolPorts.All, ct).ConfigureAwait(false);
}
