namespace Agentivity.Infrastructure.Agentic.Agent.Policies;

public static class AgentPolicyLayerFactory
{
    public const int PlatformDefaultPriority = 0;
    public const int RuntimeAgentDefaultPriority = 100;
    public const int RuntimeModeDefaultPriority = 200;
    public const int ResourceDefaultPriority = 300;
    public const int ExplicitUserPolicyPriority = 1000;

    public static IReadOnlyList<AgentPolicyLayer> BuildPolicyLayers(IAgenticModeRuntime modeRuntime, IReadOnlyList<AgentPolicyLayer>? externalLayers = null)
    {
        ArgumentNullException.ThrowIfNull(modeRuntime);

        var layers = new List<AgentPolicyLayer>
        {
            new(AgentPolicyScope.Platform, "platform-default", PlatformDefaultPriority, new AgentPolicySet(Workspace: new AgentWorkspacePolicySet(StoreOnlyContextualizedFacts: true, DisallowAssistantSuggestionPromotion: true, PreserveOpenQuestions: true))),
            new(
                AgentPolicyScope.Agent,
                "ai-agent-default",
                RuntimeAgentDefaultPriority,
                new AgentPolicySet(HumanLoop: new AgentHumanLoopPolicySet(AskHumanOnlyForMaterialUnknowns: true), Planning: new AgentPlanningPolicySet(RequireUnknownsToBeRepresented: true), TextualRules: [AgentPromptPolicyRuntime.RequireUnknownsToBeRepresentedRule])
            ),
            new(AgentPolicyScope.Mode, $"mode-{modeRuntime.ModeId}", RuntimeModeDefaultPriority, modeRuntime.DefaultPolicies),
        };

        if (externalLayers is { Count: > 0 })
        {
            layers.AddRange(externalLayers);
        }

        return layers;
    }
}
