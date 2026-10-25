using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Agentic.Agent;

public interface IAgentGuardrailSource
{
    AgentGuardrailDefinition? BuildGuardrail(ResourceBinding binding);
}
