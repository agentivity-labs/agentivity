using Agentivity.Infrastructure.Agentic.Agent;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Standalone implementation of <see cref="IAgentTraceProxy"/>.
/// The standalone execution path does not write workflow-style execution traces,
/// so all methods are no-ops. Delegates to <see cref="IAgentTraceProxy.Null"/>.
/// </summary>
internal sealed class StandaloneTraceProxy : IAgentTraceProxy
{
    public static readonly StandaloneTraceProxy Instance = new();

    private StandaloneTraceProxy() { }

    public void Initialize(AgentTraceMetadata metadata) { }

    public void AppendEvent(AgentTraceMetadata metadata, AgentTraceEvent evt) { }

    public void UpsertContextLayersSnapshot(AgentTraceMetadata metadata, int iteration, string snapshot) { }
}
