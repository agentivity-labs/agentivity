namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public interface IAgentPolicyRuntime
{
    /// <summary>
    /// Applies human-loop policy gates to a "does this reply need a human answer" signal, regardless
    /// of how that signal was obtained (folded into the main turn, or from the separate
    /// <see cref="Agentivity.Infrastructure.Agentic.Agent.AgentSchemas.MissedAskHumanFallback"/> call).
    /// </summary>
    HumanLoopPolicyDecision EvaluateHumanInputPolicy(bool needsHumanInput, bool isSelectionQuestion, ResolvedAgentPolicies policies);

    AgentWorkspacePolicyOptions BuildAgentWorkspacePolicyOptions(ResolvedAgentPolicies policies);

    IReadOnlyList<string> BuildAgentWorkspacePromptRules(ResolvedAgentPolicies policies);

    IReadOnlyList<string> BuildAgentWorkspaceConsolidationRules(ResolvedAgentPolicies policies);

    /// <summary>
    /// Returns prompt rules for the given policy context.
    /// <paramref name="context"/> maps to <see cref="AgentPolicyContext"/> constants.
    /// Returns an empty list for unrecognised context keys.
    /// </summary>
    IReadOnlyList<string> GetPolicyRules(string context, ResolvedAgentPolicies policies);
}

public sealed class AgentPolicyRuntime : IAgentPolicyRuntime
{
    private readonly AgentPromptPolicyRuntime _promptPolicyRuntime = new();

    /// <summary>
    /// Default no-op resolved policies — all boolean flags false, no textual rules.
    /// Used when no policies are configured (e.g. standalone agent without policy assignment).
    /// </summary>
    public static readonly ResolvedAgentPolicies DefaultPolicies = new(
        Effective: new ResolvedAgentPolicySet(
            Conversation: new ResolvedAgentConversationPolicies(false, false, false),
            Planning: new ResolvedAgentPlanningPolicies(false, false),
            HumanLoop: new ResolvedAgentHumanLoopPolicies(false, false),
            Workspace: new ResolvedAgentWorkspacePolicies(false, false, false)
        ),
        Entries: [],
        Layers: [],
        TextualRules: null
    );

    public HumanLoopPolicyDecision EvaluateHumanInputPolicy(bool needsHumanInput, bool isSelectionQuestion, ResolvedAgentPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        if (!needsHumanInput)
        {
            return new HumanLoopPolicyDecision(false);
        }

        // The agent has no reliable way to attach decision context to a selection question once the
        // question is already being asked, so this policy simply refuses to interrupt on selection
        // questions rather than asking with incomplete context.
        var requiresDecisionContext = policies.Effective.HumanLoop.RequireOptionsBeforeSelection || policies.Effective.Conversation.RequireDecisionContextBeforeHumanQuestion;
        if (requiresDecisionContext && isSelectionQuestion)
        {
            return new HumanLoopPolicyDecision(false, "The active human-loop policies require decision context before asking the human to choose between alternatives.");
        }

        return new HumanLoopPolicyDecision(true);
    }

    public AgentWorkspacePolicyOptions BuildAgentWorkspacePolicyOptions(ResolvedAgentPolicies policies) => _promptPolicyRuntime.BuildAgentWorkspacePolicyOptions(policies);

    public IReadOnlyList<string> BuildAgentWorkspacePromptRules(ResolvedAgentPolicies policies) => _promptPolicyRuntime.BuildAgentWorkspacePromptRules(policies);

    public IReadOnlyList<string> BuildAgentWorkspaceConsolidationRules(ResolvedAgentPolicies policies) => _promptPolicyRuntime.BuildAgentWorkspaceConsolidationRules(policies);

    public IReadOnlyList<string> GetPolicyRules(string context, ResolvedAgentPolicies policies) =>
        context switch
        {
            AgentPolicyContext.Planning => _promptPolicyRuntime.BuildPlanningPromptRules(policies),
            AgentPolicyContext.Execution => _promptPolicyRuntime.BuildExecutionPromptRules(policies),
            AgentPolicyContext.HumanLoop => _promptPolicyRuntime.BuildHumanLoopPromptRules(policies),
            AgentPolicyContext.TaskBatch => _promptPolicyRuntime.BuildTaskBatchPromptRules(policies),
            AgentPolicyContext.Join => _promptPolicyRuntime.BuildJoinPromptRules(policies),
            AgentPolicyContext.Replanning => _promptPolicyRuntime.BuildObservationReplanningPromptRules(policies),
            AgentPolicyContext.PlanRefinement => _promptPolicyRuntime.BuildPlanRefinementPromptRules(policies),
            AgentPolicyContext.Workspace => _promptPolicyRuntime.BuildAgentWorkspacePromptRules(policies),
            AgentPolicyContext.WorkspaceConsolidation => _promptPolicyRuntime.BuildAgentWorkspaceConsolidationRules(policies),
            _ => [],
        };
}
