using System.Diagnostics;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Observability.Runtime;

/// <summary>
/// Default <see cref="IAgentTraceEmitter"/> — fans out execution trace events to
/// <see cref="IAgentTraceSink"/> implementations only (e.g. <c>DebugTraceProjectionSink</c>).
/// <para>
/// Deliberately separate from <see cref="AgentTelemetryEmitter"/>: traces are not telemetry
/// and must not share sinks, registration, or this emitter with the OpenTelemetry export.
/// </para>
/// </summary>
public sealed class AgentTraceEmitter(IEnumerable<IAgentTraceSink> sinks) : IAgentTraceEmitter
{
    private const string CurrentSchemaVersion = "1.0";
    private readonly IReadOnlyList<IAgentTraceSink> _sinks = sinks.ToList();

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
                Trace.TraceWarning("Trace sink {0} failed while emitting {1}. The failure was ignored. Error: {2}", sink.GetType().FullName, envelope.EventName, ex.Message);
            }
        }
    }
}
