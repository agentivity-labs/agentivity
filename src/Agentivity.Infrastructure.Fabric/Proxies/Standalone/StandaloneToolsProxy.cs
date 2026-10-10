using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Standalone implementation of <see cref="IAgentToolsProxy"/>.
/// Wraps <see cref="StandaloneAgentToolProxy"/> — the standalone equivalent of <c>McpToolRouter</c>.
/// AG-UI custom widgets aren't part of this tool catalog at all — they reach the LLM via the chat
/// channel's <c>interact_with_human</c> schema (see <c>ChatChannel.ResolveInteractionWidgets</c>),
/// which already applies uniformly to every entity kind.
/// </summary>
internal sealed class StandaloneToolsProxy(StandaloneAgentToolProxy? toolPort, bool allowNeedsInput = true) : IAgentToolsProxy
{
    public async ValueTask<IReadOnlyList<JsonObject>> ListConnectedToolsAsync(CancellationToken ct)
    {
        if (toolPort is null)
            return [];
        return await toolPort.ListToolsAsync(ct).ConfigureAwait(false);
    }

    public bool AllowsNeedsInput => allowNeedsInput;

    /// <summary>Returns the underlying tool port, for use by the execution runtime port.</summary>
    public StandaloneAgentToolProxy? ToolPort => toolPort;
}
