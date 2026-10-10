using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Generic telemetry surface passed to agent runtimes via <see cref="IAgentManifest.Telemetry"/>.
/// Runtimes emit structured events directly without needing infrastructure helpers.
/// </summary>
public interface IAgentTelemetry
{
    /// <summary>Emits a strongly-typed telemetry event with the current execution correlation.</summary>
    ValueTask EmitAsync<TEvent>(TEvent payload, string stepId, CancellationToken cancellationToken = default)
        where TEvent : class, IAgentTelemetryEvent;
}
