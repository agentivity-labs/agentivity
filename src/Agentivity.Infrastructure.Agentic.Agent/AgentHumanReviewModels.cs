using Agentivity.Infrastructure.Workflows.Interactions;

namespace Agentivity.Infrastructure.Agentic.Agent;

public enum AgentHumanReviewKind
{
    AskHuman,
    Plan,
    ToolCall,
    Response,
}

public sealed record AgentPendingToolCall(string Id, string Name, string ArgumentsJson);

public sealed record PendingHumanReview(
    AgentHumanReviewKind Kind,
    Guid RequestId,
    int Iteration,
    string Title,
    string? Question,
    List<string> QuestionsToAsk,
    string? DraftContent,
    string? AssistantDraft,
    string? ToolCallId,
    string? ToolName,
    string? ToolArgumentsJson,
    InteractionKind InteractionKind,
    HumanInteractionOperation Operation,
    List<InteractionField> Fields,
    AgentHumanLoopResultMode ResultMode,
    List<string> ClarificationKeys,
    List<AgentPendingToolCall> RemainingToolCalls,
    DateTimeOffset CreatedAtUtc
);
