using System.Diagnostics;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Observability.Runtime;

public sealed class AgentTelemetryEmitter(IEnumerable<IAgentTelemetrySink> sinks) : IAgentTelemetryEmitter
{
    private const string CurrentSchemaVersion = "1.0";
    private readonly IReadOnlyList<IAgentTelemetrySink> _sinks = sinks.ToList();

    public async ValueTask EmitAsync<TEvent>(ExecutionCorrelationContext correlation, TEvent payload, CancellationToken cancellationToken = default)
        where TEvent : class, IAgentTelemetryEvent
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(payload);

        var envelope = new AgentTelemetryEnvelope(CurrentSchemaVersion, payload.Kind, typeof(TEvent).Name, DateTime.UtcNow, correlation, payload);

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Telemetry sink {0} failed while emitting {1}. The failure was ignored because telemetry is best-effort and must not crash workflow execution. Error: {2}", sink.GetType().FullName, envelope.EventName, ex.Message);
            }
        }
    }
}
