namespace Agentivity.Observability.Contracts.Events.AgentControl;

public sealed record HumanInterventionRequestedEvent(string QuestionId, string Question, DateTime RequestedAtUtc, string? QuestionContext = null, string? ChannelType = null) : IAgentTelemetryEvent
{
    public AgentTelemetryEventKind Kind => AgentTelemetryEventKind.HumanInterventionRequested;
}
