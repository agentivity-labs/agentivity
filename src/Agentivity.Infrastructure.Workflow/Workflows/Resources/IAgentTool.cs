using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Contract for tools that can be called dynamically by <c>AiAgentNode</c>.
/// This contract is intentionally separate from other tool families such as content processing tools.
/// </summary>
public interface IAgentTool
{
    /// <summary>
    /// Returns all MCP tool definitions exposed by this resource.
    /// </summary>
    ValueTask<IReadOnlyList<JsonObject>> ListToolsAsync(ResourceBinding binding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles an MCP tool call using the JSON arguments emitted by the LLM.
    /// </summary>
    /// <remarks>
    /// <para><strong>Error handling contract — all implementations must follow this:</strong></para>
    /// <list type="number">
    ///   <item>
    ///     <term>Recoverable error with explicit reason</term>
    ///     <description>
    ///       Return <see cref="McpToolResultJson.CreateFailedResult"/> with a short machine-readable
    ///       <c>failedReason</c> (e.g. <c>"unsupported_operator"</c>, <c>"rate_limit"</c>,
    ///       <c>"invalid_expression"</c>) and a human-readable message that explains exactly
    ///       what went wrong and how to fix it. The join LLM uses this signal to replan
    ///       with corrected parameters. Use this when replanning can realistically recover.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Unrecoverable failure</term>
    ///     <description>
    ///       Throw an exception. The infrastructure catches it, records it in the trace for
    ///       diagnostics, and stops the agent immediately — no join, no replan.
    ///       Use this when no amount of replanning can fix the problem
    ///       (e.g. wrong tool name dispatched, permanently unavailable dependency,
    ///       programming contract violation).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Do NOT swallow exceptions</term>
    ///     <description>
    ///       Do not catch and silently convert unexpected exceptions into <c>isError: true</c>
    ///       results. That hides root causes and prevents the agent from stopping cleanly.
    ///       Only catch exceptions you can meaningfully classify and describe.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>
    ///   Error messages must be actionable: state what failed, why, and what the caller
    ///   should do differently. A bare exception message is not sufficient.
    /// </para>
    /// </remarks>
    ValueTask<JsonObject> CallToolAsync(ResourceBinding binding, string toolName, JsonElement arguments, CancellationToken cancellationToken = default);
}
