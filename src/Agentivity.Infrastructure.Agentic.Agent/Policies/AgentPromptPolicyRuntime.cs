namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public sealed class AgentPromptPolicyRuntime
{
    public const string RequireUnknownsToBeRepresentedRule = "Represent material unknowns explicitly instead of guessing or silently collapsing them.";
    public const string RequireDecisionPointsWhenAlternativesExistRule = "When materially different alternatives exist, represent the decision point explicitly instead of converging silently on one option.";
    public const string DisallowPrematureConvergenceRule = "Do not converge on a single recommendation before the available work justifies that convergence.";
    public const string RequireExplicitAlternativesBeforeChoiceRule = "If a later step may ask the human to choose, preserve the relevant alternatives with enough decision context before asking for a choice.";

    /// <summary>
    /// Prompt rules guiding when a reply should set <c>needs_human_input</c> on
    /// <see cref="Agentivity.Infrastructure.Agentic.Agent.AgentSchemas.ConversationalReply"/>.
    /// </summary>
    public IReadOnlyList<string> BuildHumanLoopPromptRules(ResolvedAgentPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var rules = new List<string>();
        if (policies.Effective.HumanLoop.AskHumanOnlyForMaterialUnknowns)
        {
            rules.Add("Set `needs_human_input` to true only when the missing information is materially important for the next step or decision — not for trivial clarifications.");
        }

        AppendTextualRules(rules, policies);
        return rules;
    }

    public AgentWorkspacePolicyOptions BuildAgentWorkspacePolicyOptions(ResolvedAgentPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        return new AgentWorkspacePolicyOptions(
            StoreOnlyContextualizedFacts: policies.Effective.Workspace.StoreOnlyContextualizedFacts,
            DisallowAssistantSuggestionPromotion: policies.Effective.Workspace.DisallowAssistantSuggestionPromotion,
            PreserveOpenQuestions: policies.Effective.Workspace.PreserveOpenQuestions
        );
    }

    public IReadOnlyList<string> BuildAgentWorkspacePromptRules(ResolvedAgentPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var rules = new List<string>();
        if (policies.Effective.Workspace.StoreOnlyContextualizedFacts)
        {
            rules.Add("Keep only contextualized facts in the agent workspace. Avoid terse values that lose their meaning outside the surrounding conversation.");
        }

        if (policies.Effective.Workspace.DisallowAssistantSuggestionPromotion)
        {
            rules.Add("Do not treat assistant suggestions or provisional recommendations as established facts unless the conversation explicitly confirms them.");
        }

        if (policies.Effective.Workspace.PreserveOpenQuestions)
        {
            rules.Add("Preserve unresolved questions as open questions instead of converting them into defaults, decisions, or facts.");
        }

        AppendTextualRules(rules, policies);
        return rules;
    }

    public IReadOnlyList<string> BuildAgentWorkspaceConsolidationRules(ResolvedAgentPolicies policies) => BuildAgentWorkspacePromptRules(policies);

    public IReadOnlyList<string> BuildPlanningPromptRules(ResolvedAgentPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var rules = new List<string> { BuildPromptAuthorityOrderingRule() };
        if (policies.Effective.Planning.RequireUnknownsToBeRepresented)
        {
            rules.Add(RequireUnknownsToBeRepresentedRule);
        }

        if (policies.Effective.Planning.RequireDecisionPointsWhenAlternativesExist)
        {
            rules.Add(RequireDecisionPointsWhenAlternativesExistRule);
        }

        if (policies.Effective.Conversation.DisallowPrematureConvergence)
        {
            rules.Add(DisallowPrematureConvergenceRule);
            rules.Add("Prefer an exploration -> compare -> recommend flow over asking for strict precision before the graph has surfaced viable options.");
        }

        if (policies.Effective.Conversation.RequireExplicitAlternativesBeforeChoice)
        {
            rules.Add(RequireExplicitAlternativesBeforeChoiceRule);
        }

        AppendTextualRules(rules, policies);
        return rules;
    }

    public IReadOnlyList<string> BuildExecutionPromptRules(ResolvedAgentPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var rules = new List<string> { BuildPromptAuthorityOrderingRule() };
        if (policies.Effective.Planning.RequireUnknownsToBeRepresented)
        {
            rules.Add(RequireUnknownsToBeRepresentedRule);
            rules.Add("If execution reveals material unknowns, keep them explicit in your observations instead of hiding them behind a confident recommendation.");
        }

        if (policies.Effective.Conversation.DisallowPrematureConvergence)
        {
            rules.Add(DisallowPrematureConvergenceRule);
            rules.Add("Do not silently converge on a single recommendation unless the executed work already justifies that conclusion.");
        }

        if (policies.Effective.Conversation.RequireExplicitAlternativesBeforeChoice)
        {
            rules.Add(RequireExplicitAlternativesBeforeChoiceRule);
            rules.Add("Preserve the relevant alternatives and their trade-offs in the observations instead of collapsing them prematurely.");
        }

        if (policies.Effective.HumanLoop.RequireOptionsBeforeSelection)
        {
            rules.Add("If exact precision is not yet required, continue exploring and comparing viable options before asking the human to lock in a single precise value.");
        }

        AppendTextualRules(rules, policies);
        return rules;
    }

    public IReadOnlyList<string> BuildTaskBatchPromptRules(ResolvedAgentPolicies policies) => BuildExecutionPromptRules(policies);

    public IReadOnlyList<string> BuildJoinPromptRules(ResolvedAgentPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var rules = new List<string>();
        if (policies.Effective.Conversation.DisallowPrematureConvergence)
        {
            rules.Add(DisallowPrematureConvergenceRule);
            rules.Add("Do not finalize if the current answer converges on a recommendation that has not been justified by the completed observations.");
        }

        if (policies.Effective.Conversation.RequireExplicitAlternativesBeforeChoice)
        {
            rules.Add(RequireExplicitAlternativesBeforeChoiceRule);
            rules.Add("If the answer would require the human to choose between alternatives, only finalize when the relevant alternatives and their key differences are already established in the completed observations.");
        }

        if (policies.Effective.HumanLoop.RequireOptionsBeforeSelection)
        {
            rules.Add("If the current answer would ask for exact values or a final choice before viable options have been surfaced, prefer `replan` over premature finalization.");
        }

        if (policies.Effective.Planning.RequireUnknownsToBeRepresented)
        {
            rules.Add(RequireUnknownsToBeRepresentedRule);
            rules.Add("If material unknowns remain unresolved, prefer `replan` over pretending the answer is complete.");
        }

        AppendTextualRules(rules, policies);
        return rules;
    }

    public IReadOnlyList<string> BuildObservationReplanningPromptRules(ResolvedAgentPolicies policies) => BuildPlanningPromptRules(policies);

    public IReadOnlyList<string> BuildPlanRefinementPromptRules(ResolvedAgentPolicies policies) => BuildPlanningPromptRules(policies);

    public static string BuildPromptAuthorityOrderingRule() =>
        "When multiple context layers are present, apply this authority order: latest explicit conversation > active clarification ledger > current execution or graph state > restored recent messages > agent workspace > session summary > external memory. Do not let a weaker layer override a stronger one.";

    private static void AppendTextualRules(List<string> rules, ResolvedAgentPolicies policies)
    {
        if (policies.TextualRules is not { Count: > 0 })
        {
            return;
        }

        var seenRules = new HashSet<string>(rules, StringComparer.Ordinal);

        foreach (var rule in policies.TextualRules)
        {
            if (string.IsNullOrWhiteSpace(rule))
            {
                continue;
            }

            var normalizedRule = rule.Trim();
            if (!seenRules.Add(normalizedRule))
            {
                continue;
            }

            rules.Add(normalizedRule);
        }
    }
}
