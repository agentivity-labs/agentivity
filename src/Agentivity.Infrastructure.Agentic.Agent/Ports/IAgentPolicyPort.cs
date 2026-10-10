using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Port politique d'un run agent.
/// Abstrait la résolution et l'évaluation des policies, qu'elles viennent
/// d'un workflow (<c>WorkflowAgentPolicyPort</c>) ou d'un agent standalone
/// (<c>ComposedAgentPolicyPort</c>).
/// <para>
/// Câblé dans <see cref="RunContext.Policy"/>.
/// </para>
/// </summary>
public interface IAgentPolicyPort
{
    /// <summary>
    /// Returns prompt rules for the given policy context key
    /// (see <see cref="AgentPolicyContext"/> constants).
    /// Returns an empty list for unrecognised context keys.
    /// </summary>
    IReadOnlyList<string> GetRules(string context);
}
