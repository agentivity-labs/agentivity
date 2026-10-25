namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public enum AgentPolicyScope
{
    Platform,
    Resource,
    Agent,
    Mode,
    Run,
}

public sealed record AgentPolicyLayer(AgentPolicyScope Scope, string SourceId, int Priority, AgentPolicySet Policies);

public sealed record AgentPolicySet(AgentConversationPolicySet? Conversation = null, AgentPlanningPolicySet? Planning = null, AgentHumanLoopPolicySet? HumanLoop = null, AgentWorkspacePolicySet? Workspace = null, IReadOnlyList<string>? TextualRules = null);

public sealed record AgentConversationPolicySet(bool? RequireExplicitAlternativesBeforeChoice = null, bool? DisallowPrematureConvergence = null, bool? RequireDecisionContextBeforeHumanQuestion = null);

public sealed record AgentPlanningPolicySet(bool? RequireUnknownsToBeRepresented = null, bool? RequireDecisionPointsWhenAlternativesExist = null);

public sealed record AgentHumanLoopPolicySet(bool? AskHumanOnlyForMaterialUnknowns = null, bool? RequireOptionsBeforeSelection = null);

public sealed record AgentWorkspacePolicySet(bool? StoreOnlyContextualizedFacts = null, bool? DisallowAssistantSuggestionPromotion = null, bool? PreserveOpenQuestions = null);

public sealed record ResolvedAgentPolicies(ResolvedAgentPolicySet Effective, IReadOnlyList<ResolvedAgentPolicyEntry> Entries, IReadOnlyList<AgentPolicyLayer> Layers, IReadOnlyList<string>? TextualRules = null);

public sealed record ResolvedAgentPolicySet(ResolvedAgentConversationPolicies Conversation, ResolvedAgentPlanningPolicies Planning, ResolvedAgentHumanLoopPolicies HumanLoop, ResolvedAgentWorkspacePolicies Workspace);

public sealed record ResolvedAgentConversationPolicies(bool RequireExplicitAlternativesBeforeChoice, bool DisallowPrematureConvergence, bool RequireDecisionContextBeforeHumanQuestion);

public sealed record ResolvedAgentPlanningPolicies(bool RequireUnknownsToBeRepresented, bool RequireDecisionPointsWhenAlternativesExist);

public sealed record ResolvedAgentHumanLoopPolicies(bool AskHumanOnlyForMaterialUnknowns, bool RequireOptionsBeforeSelection);

public sealed record ResolvedAgentWorkspacePolicies(bool StoreOnlyContextualizedFacts, bool DisallowAssistantSuggestionPromotion, bool PreserveOpenQuestions);

public sealed record ResolvedAgentPolicyEntry(string Category, string Name, string Value, AgentPolicyScope Scope, string SourceId);
