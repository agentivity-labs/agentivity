using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public interface IAgentPolicySource
{
    AgentPolicyLayer? BuildPolicyLayer(ResourceBinding binding);
}
