namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public sealed record AgentWorkspacePolicyOptions(bool StoreOnlyContextualizedFacts, bool DisallowAssistantSuggestionPromotion, bool PreserveOpenQuestions);
