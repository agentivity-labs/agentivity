using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public static class AgentPolicyResourceResolver
{
    public static IReadOnlyList<AgentPolicyLayer> ResolveConnectedPolicyLayers(NodeConfig context)
    {
        var policySources = ResourceResolver.ResolveToolNodes<IAgentPolicySource>(context, "policies", ResourcePortCategory.AIAgentPolicy);
        if (policySources.Count == 0)
        {
            return [];
        }

        return policySources.Select(resolved => resolved.Tool.BuildPolicyLayer(resolved.Binding)).Where(layer => layer is not null).Select(layer => layer!).ToList();
    }
}
