using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Observability.Sinks;

public sealed class InMemoryTelemetrySink : IAgentTelemetrySink
{
    private readonly List<AgentTelemetryEnvelope> _events = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<AgentTelemetryEnvelope> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    public ValueTask WriteAsync(AgentTelemetryEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            _events.Add(envelope);
        }

        return ValueTask.CompletedTask;
    }
}
