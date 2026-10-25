using Agentivity.Infrastructure.Workflows.Interactions;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record PreparedAgentHumanLoopContext(bool HasInteractionChannel, AgentHumanLoopCapabilities Capabilities)
{
    public bool IsHumanLoopEnabledFor(SessionContext sessionContext)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);
        return sessionContext.Enabled && HasInteractionChannel && Capabilities.SupportsAskHuman;
    }
}
