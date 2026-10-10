using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Port guardrail d'un run agent.
/// Abstrait l'application des guardrails, qu'ils viennent
/// d'un workflow (<c>WorkflowAgentGuardrailPort</c>) ou d'un agent standalone
/// (<c>ComposedAgentGuardrailPort</c>).
/// <para>
/// Câblé dans <see cref="RunContext.Guardrail"/>.
/// </para>
/// </summary>
public interface IAgentGuardrailPort
{
    /// <summary>
    /// Applies guardrails for the given stage key (see <see cref="AgentGuardrailContext"/> constants).
    /// Returns the (possibly rewritten) content, or throws when a guardrail blocks execution.
    /// </summary>
    /// <param name="matchSink">When supplied, every non-blocking (Warn) match found gets appended
    /// here — see <see cref="AgentRunServices.GuardrailWarningSink"/>.</param>
    ValueTask<string> ApplyAsync(string stage, string content, List<AgentGuardrailMatch>? matchSink = null, CancellationToken cancellationToken = default);
}
