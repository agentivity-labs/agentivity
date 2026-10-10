using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed class AgentGuardrailResolver
{
    public IReadOnlyList<AgentGuardrailDefinition> Resolve(NodeConfig context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sources = ResourceResolver.ResolveToolNodes<IAgentGuardrailSource>(context, "guardrails", ResourcePortCategory.AIAgentGuardrail);
        if (sources.Count == 0)
        {
            return [];
        }

        return sources.Select(source => source.Tool.BuildGuardrail(source.Binding)).Where(guardrail => guardrail is not null).Select(guardrail => guardrail!).OrderBy(guardrail => guardrail.Priority).ThenBy(guardrail => guardrail.GuardrailId, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
