using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Observability.Abstractions;

/// <summary>
/// Emits execution trace events to <see cref="IAgentTraceSink"/> implementations
/// (e.g. <c>DebugTraceProjectionSink</c>) for the server's own debug trace view.
/// <para>
/// This is a SEPARATE pipeline from <see cref="IAgentTelemetryEmitter"/>: traces are not
/// telemetry. They must not be routed through <see cref="IAgentTelemetrySink"/> (the OpenTelemetry export)
/// nor through <c>IRunEventBus</c> (chat/SSE). Callers that need both telemetry and a debug
/// trace projection for the same event must inject and call both emitters explicitly.
/// </para>
/// </summary>
public interface IAgentTraceEmitter
{
    ValueTask EmitAsync<TEvent>(ExecutionCorrelationContext correlation, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : class, IAgentTelemetryEvent;
}
