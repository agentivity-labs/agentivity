using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Observability.Abstractions;

public interface IAgentTelemetrySink
{
    ValueTask WriteAsync(AgentTelemetryEnvelope envelope, CancellationToken cancellationToken = default);
}
