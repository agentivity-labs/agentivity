namespace Agentivity.Observability.Contracts.Events;

public enum AgentTelemetryEventKind
{
    RunStarted,
    RunCompleted,
    RunFailed,
    NodeStarted,
    NodeCompleted,
    NodeFailed,
    AgentStarted,
    AgentCompleted,
    AgentFailed,
    LlmCallStarted,
    LlmCallCompleted,
    ToolCallStarted,
    ToolCallCompleted,
    ReplanTriggered,
    HumanInterventionRequested,
    HumanInterventionCompleted,
    ErrorRaised,
    PlanCreated,
    TaskBatchStarted,
    GraphReplanned,
    PlanQualityScore,
    CustomWidgetInvoked,
}
