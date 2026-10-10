namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record HumanReviewResumeState(IReadOnlyList<AgentToolExecution> ExecutedToolCalls, IReadOnlyList<AgentPendingToolCall> PendingToolCalls, string? FinalResponse, string? FinishReason, string? AssistantDraft, string? ContinuationContext, int IterationsCompleted)
{
    public static HumanReviewResumeState None { get; } = new([], [], null, null, null, null, 0);
}
