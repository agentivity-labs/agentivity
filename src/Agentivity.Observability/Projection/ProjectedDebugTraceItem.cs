namespace Agentivity.Observability.Projection;

public sealed record ProjectedDebugTraceItem(
    string Kind,
    string Name,
    string? Message,
    ProjectedDebugTraceStatus Status,
    IReadOnlyList<ProjectedDebugTraceField>? Fields = null,
    IReadOnlyList<ProjectedDebugTraceItem>? Children = null,
    string? StepId = null,
    string? SpanId = null,
    string? CorrelationKey = null
);

public sealed record ProjectedDebugTraceField(string Name, string Value);

public enum ProjectedDebugTraceStatus
{
    Info,
    Warning,
    Error,
}
