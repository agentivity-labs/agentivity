using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Policies;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Standalone implementation of <see cref="IAgentPolicyPort"/>.
/// Wraps a pre-resolved <see cref="ResolvedAgentPolicies"/> built from
/// <see cref="AgentEntity.Graph"/> bindings by <see cref="AgentGraphResolver"/>.
/// </summary>
internal sealed class ComposedAgentPolicyProxy : IAgentPolicyPort
{
    private readonly IAgentPolicyRuntime _runtime;
    private readonly ResolvedAgentPolicies _policies;

    public ComposedAgentPolicyProxy(IAgentPolicyRuntime runtime, ResolvedAgentPolicies policies)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    public IReadOnlyList<string> GetRules(string context) => _runtime.GetPolicyRules(context, _policies);
}
