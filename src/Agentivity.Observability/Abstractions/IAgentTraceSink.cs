using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Observability.Abstractions;

/// <summary>
/// Receives execution trace projections (e.g. <c>DebugTraceProjectionSink</c>) for surfacing
/// step-by-step run/node debug traces back to the server's own UI.
/// <para>
/// This is intentionally a SEPARATE abstraction from <see cref="IAgentTelemetrySink"/>:
/// traces are NOT telemetry. Telemetry sinks (<see cref="IAgentTelemetrySink"/>) export
/// monitoring data to external observability backends (e.g. BetterStack via OpenTelemetry).
/// Trace sinks project the same underlying envelopes into an in-process debug trace tree
/// consumed by the server itself. The two must never be conflated or share a registration.
/// </para>
/// </summary>
public interface IAgentTraceSink
{
    ValueTask WriteAsync(AgentTelemetryEnvelope envelope, CancellationToken cancellationToken = default);
}
