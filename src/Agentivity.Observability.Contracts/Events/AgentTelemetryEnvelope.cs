using Agentivity.Observability.Contracts.Correlation;

namespace Agentivity.Observability.Contracts.Events;

public sealed record AgentTelemetryEnvelope(string SchemaVersion, AgentTelemetryEventKind Kind, string EventName, DateTime OccurredAtUtc, ExecutionCorrelationContext Correlation, object Payload);
