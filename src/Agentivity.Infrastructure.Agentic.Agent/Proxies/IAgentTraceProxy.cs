namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Proxy for execution trace (observability/debug) operations.
/// No-op implementations are used in contexts without tracing (standalone, team).
/// </summary>
public interface IAgentTraceProxy
{
    /// <summary>Initialises the execution trace at the start of a run.</summary>
    void Initialize(AgentTraceMetadata metadata);

    /// <summary>Appends a trace event (policy/guardrail resolution, tool call, iteration, etc.).</summary>
    void AppendEvent(AgentTraceMetadata metadata, AgentTraceEvent evt);

    /// <summary>Updates the context-layers snapshot in the trace for the given iteration.</summary>
    void UpsertContextLayersSnapshot(AgentTraceMetadata metadata, int iteration, string snapshot);

    /// <summary>A no-op proxy that silently discards all trace calls.</summary>
    static IAgentTraceProxy Null { get; } = new NullAgentTraceProxy();

    private sealed class NullAgentTraceProxy : IAgentTraceProxy
    {
        public void Initialize(AgentTraceMetadata metadata) { }

        public void AppendEvent(AgentTraceMetadata metadata, AgentTraceEvent evt) { }

        public void UpsertContextLayersSnapshot(AgentTraceMetadata metadata, int iteration, string snapshot) { }
    }
}
