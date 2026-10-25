using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Observability.Abstractions;

public interface IAgentTelemetryEmitter
{
    ValueTask EmitAsync<TEvent>(ExecutionCorrelationContext correlation, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : class, IAgentTelemetryEvent;
}
