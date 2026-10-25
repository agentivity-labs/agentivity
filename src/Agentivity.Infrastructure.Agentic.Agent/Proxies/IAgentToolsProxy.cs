using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Proxy for resolving an agent's tool catalog at preparation time.
/// Each execution context (workflow, team, standalone) provides its own implementation.
/// </summary>
public interface IAgentToolsProxy
{
    /// <summary>Returns connected tool definitions (MCP, AI agent tool nodes, etc.).</summary>
    ValueTask<IReadOnlyList<JsonObject>> ListConnectedToolsAsync(CancellationToken ct);

    /// <summary>
    /// A context-specific turn-conclusion tool (<c>agent_next_step</c>), or null to use the host's default
    /// one — a plain done / needs_input decision. A team engine supplies its own, with orchestration choices.
    /// <para>
    /// Deliberately kept out of <see cref="ListConnectedToolsAsync"/>: it is never offered to the
    /// agent's own turn (that would let a small model quietly "fill the form" instead of doing its
    /// actual work). It is only ever invoked as a single, constrained, forced follow-up call once
    /// a turn ends in plain text (see <c>AgentManifest.ResolveTurnConclusionAsync</c>).
    /// </para>
    /// </summary>
    ITurnConclusionTool? GetTurnConclusionTool() => null;

    /// <summary>Whether the default turn-conclusion tool may offer <c>needs_input</c> (this agent can wait for a human reply).</summary>
    bool AllowsNeedsInput => true;
}
