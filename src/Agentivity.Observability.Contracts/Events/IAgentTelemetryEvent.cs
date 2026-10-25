namespace Agentivity.Observability.Contracts.Events;

public interface IAgentTelemetryEvent
{
    AgentTelemetryEventKind Kind { get; }
}
