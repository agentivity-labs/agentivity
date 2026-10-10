namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public sealed class AgentPolicyResolver
{
    public ResolvedAgentPolicies Resolve(IReadOnlyList<AgentPolicyLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var orderedLayers = layers.OrderBy(layer => layer.Priority).ThenBy(layer => layer.Scope).ToList();

        var conversation = new ResolvedAgentConversationPolicies(RequireExplicitAlternativesBeforeChoice: false, DisallowPrematureConvergence: false, RequireDecisionContextBeforeHumanQuestion: false);
        var planning = new ResolvedAgentPlanningPolicies(RequireUnknownsToBeRepresented: false, RequireDecisionPointsWhenAlternativesExist: false);
        var humanLoop = new ResolvedAgentHumanLoopPolicies(AskHumanOnlyForMaterialUnknowns: false, RequireOptionsBeforeSelection: false);
        var knowledgeBase = new ResolvedAgentWorkspacePolicies(StoreOnlyContextualizedFacts: false, DisallowAssistantSuggestionPromotion: false, PreserveOpenQuestions: false);

        var resolvedEntries = new List<ResolvedAgentPolicyEntry>();
        var resolvedTextualRules = new List<string>();
        var seenTextualRules = new HashSet<string>(StringComparer.Ordinal);

        foreach (var layer in orderedLayers)
        {
            if (layer.Policies.Conversation is { } conversationPolicies)
            {
                if (conversationPolicies.RequireExplicitAlternativesBeforeChoice.HasValue)
                {
                    conversation = conversation with { RequireExplicitAlternativesBeforeChoice = conversationPolicies.RequireExplicitAlternativesBeforeChoice.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Conversation", "Require explicit alternatives before choice", conversationPolicies.RequireExplicitAlternativesBeforeChoice.Value.ToString(), layer.Scope, layer.SourceId));
                }

                if (conversationPolicies.DisallowPrematureConvergence.HasValue)
                {
                    conversation = conversation with { DisallowPrematureConvergence = conversationPolicies.DisallowPrematureConvergence.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Conversation", "Disallow premature convergence", conversationPolicies.DisallowPrematureConvergence.Value.ToString(), layer.Scope, layer.SourceId));
                }

                if (conversationPolicies.RequireDecisionContextBeforeHumanQuestion.HasValue)
                {
                    conversation = conversation with { RequireDecisionContextBeforeHumanQuestion = conversationPolicies.RequireDecisionContextBeforeHumanQuestion.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Conversation", "Require decision context before human question", conversationPolicies.RequireDecisionContextBeforeHumanQuestion.Value.ToString(), layer.Scope, layer.SourceId));
                }
            }

            if (layer.Policies.Planning is { } planningPolicies)
            {
                if (planningPolicies.RequireUnknownsToBeRepresented.HasValue)
                {
                    planning = planning with { RequireUnknownsToBeRepresented = planningPolicies.RequireUnknownsToBeRepresented.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Planning", "Require unknowns to be represented", planningPolicies.RequireUnknownsToBeRepresented.Value.ToString(), layer.Scope, layer.SourceId));
                }

                if (planningPolicies.RequireDecisionPointsWhenAlternativesExist.HasValue)
                {
                    planning = planning with { RequireDecisionPointsWhenAlternativesExist = planningPolicies.RequireDecisionPointsWhenAlternativesExist.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Planning", "Require decision points when alternatives exist", planningPolicies.RequireDecisionPointsWhenAlternativesExist.Value.ToString(), layer.Scope, layer.SourceId));
                }
            }

            if (layer.Policies.HumanLoop is { } humanLoopPolicies)
            {
                if (humanLoopPolicies.AskHumanOnlyForMaterialUnknowns.HasValue)
                {
                    humanLoop = humanLoop with { AskHumanOnlyForMaterialUnknowns = humanLoopPolicies.AskHumanOnlyForMaterialUnknowns.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Human loop", "Ask human only for material unknowns", humanLoopPolicies.AskHumanOnlyForMaterialUnknowns.Value.ToString(), layer.Scope, layer.SourceId));
                }

                if (humanLoopPolicies.RequireOptionsBeforeSelection.HasValue)
                {
                    humanLoop = humanLoop with { RequireOptionsBeforeSelection = humanLoopPolicies.RequireOptionsBeforeSelection.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Human loop", "Require options before selection", humanLoopPolicies.RequireOptionsBeforeSelection.Value.ToString(), layer.Scope, layer.SourceId));
                }
            }

            if (layer.Policies.Workspace is { } knowledgeBasePolicies)
            {
                if (knowledgeBasePolicies.StoreOnlyContextualizedFacts.HasValue)
                {
                    knowledgeBase = knowledgeBase with { StoreOnlyContextualizedFacts = knowledgeBasePolicies.StoreOnlyContextualizedFacts.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Knowledge base", "Store only contextualized facts", knowledgeBasePolicies.StoreOnlyContextualizedFacts.Value.ToString(), layer.Scope, layer.SourceId));
                }

                if (knowledgeBasePolicies.DisallowAssistantSuggestionPromotion.HasValue)
                {
                    knowledgeBase = knowledgeBase with { DisallowAssistantSuggestionPromotion = knowledgeBasePolicies.DisallowAssistantSuggestionPromotion.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Knowledge base", "Disallow assistant suggestion promotion", knowledgeBasePolicies.DisallowAssistantSuggestionPromotion.Value.ToString(), layer.Scope, layer.SourceId));
                }

                if (knowledgeBasePolicies.PreserveOpenQuestions.HasValue)
                {
                    knowledgeBase = knowledgeBase with { PreserveOpenQuestions = knowledgeBasePolicies.PreserveOpenQuestions.Value };
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Knowledge base", "Preserve open questions", knowledgeBasePolicies.PreserveOpenQuestions.Value.ToString(), layer.Scope, layer.SourceId));
                }
            }

            if (layer.Policies.TextualRules is { Count: > 0 })
            {
                foreach (var rule in layer.Policies.TextualRules)
                {
                    if (string.IsNullOrWhiteSpace(rule))
                    {
                        continue;
                    }

                    var normalizedRule = rule.Trim();
                    if (!seenTextualRules.Add(normalizedRule))
                    {
                        continue;
                    }

                    resolvedTextualRules.Add(normalizedRule);
                    resolvedEntries.Add(new ResolvedAgentPolicyEntry("Textual policy", "Rule", normalizedRule, layer.Scope, layer.SourceId));
                }
            }
        }

        return new ResolvedAgentPolicies(new ResolvedAgentPolicySet(conversation, planning, humanLoop, knowledgeBase), resolvedEntries, orderedLayers, resolvedTextualRules);
    }
}
