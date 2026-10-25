namespace Agentivity.Observability.Contracts.Events.AgentControl;

public sealed record ReplanTriggeredEvent(
    string Trigger,
    string Reason,
    int RevisionFrom,
    int RevisionTo,
    DateTime OccurredAtUtc,
    string? Scope = null,
    IReadOnlyList<string>? PreservedTaskIds = null,
    IReadOnlyList<string>? AffectedTaskIds = null,
    IReadOnlyList<string>? ReplanFrontierTaskIds = null,
    int? GraphRevisionCount = null,
    int? GraphTaskCount = null,
    int? GraphOutputCount = null,
    int? BlockedTaskCount = null
) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.ReplanTriggered;
}
