namespace Agentivity.Observability.Contracts.Correlation;

public sealed record ExecutionCorrelationContext(Guid RunId, string EntityId, string TraceId, string SpanId, string? ParentSpanId = null, string? NodeRunId = null, string? NodeExecutionId = null, string? StepId = null);
