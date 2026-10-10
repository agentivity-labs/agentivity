using System.Text.Json;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Policies;

/// <summary>
/// AI agent policy node. Declares structured and textual policies that are merged into the
/// AgentPolicySet at execution time.
///
/// When connected to an AiAgentNode via OnConnected, the node reconfigures its dynamic inputs
/// to reflect the PolicyLibrary declared by the connected runtime. Static inputs (scope,
/// priority, source_id, textual_policy_tree) are never removed.
/// </summary>
public sealed class AgentPolicyNode : ResourceNode, IAgentPolicySource, IWorkflowNodeLifecycle
{
    /// <summary>
    /// Prefixes used by legacy static structured fields.
    /// These are removed when a runtime with a PolicyLibrary is connected.
    /// </summary>
    private static readonly string[] LegacyPrefixes = ["conversation_", "planning_", "human_loop_", "agent_workspace_"];

    /// <summary>
    /// Input keys that must never be removed regardless of runtime changes.
    /// </summary>
    private static readonly System.Collections.Frozen.FrozenSet<string> PermanentInputs = System.Collections.Frozen.FrozenSet.ToFrozenSet(new[] { "scope", "priority", "source_id", "textual_policy_tree" }, StringComparer.OrdinalIgnoreCase);

    private const string AgentNodeType = "ai.node";
    private const string AgentModeParameter = "agent_mode";

    private readonly IEnumerable<IAgentEngine> _engines;

    public AgentPolicyNode()
        : this([]) { }

    public AgentPolicyNode(IEnumerable<IAgentEngine> engines)
        : base("ai.policies.layer")
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));

        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentPolicy);
        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Policy", ResourcePortCategory.AIAgentPolicy, maxConnections: -1);

        DefineChoice(
            "scope",
            [("platform", "Platform"), ("resource", "Resource"), ("agent", "Agent"), ("mode", "Mode"), ("run", "Run")],
            "Policy scope",
            "run",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Layer",
                Order = 10,
            }
        );
        DefineInput(
            "priority",
            ArgumentType.Number,
            "Priority used during policy resolution",
            AgentPolicyLayerFactory.ExplicitUserPolicyPriority,
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Layer",
                Order = 20,
            }
        );
        DefineInput(
            "source_id",
            ArgumentType.Text,
            "Optional policy source identifier",
            "",
            ui: new ArgumentUiMetadata
            {
                Tab = "General",
                Group = "Layer",
                Order = 30,
            }
        );
        DefineInput(
            "textual_policy_tree",
            ArgumentType.Structured,
            "Structured textual policy tree for groups and policies",
            ui: new ArgumentUiMetadata
            {
                Tab = "Textual policies",
                Group = "Rules",
                Order = 10,
                Editor = "structured",
                StructuredEditorDefinition = BuildTextualPolicyTreeEditorDefinition(),
            }
        );

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    // ----------------------------------------------------------------
    // IWorkflowNodeLifecycle
    // ----------------------------------------------------------------

    void IWorkflowNodeLifecycle.OnAdded(WorkflowEntity workflow) { }

    void IWorkflowNodeLifecycle.OnRemoved(WorkflowEntity workflow) { }

    void IWorkflowNodeLifecycle.OnDisconnected(WorkflowConnection connection, WorkflowNodeDefinition peer, WorkflowEntity workflow) { }

    void IWorkflowNodeLifecycle.OnWorkflowLoaded(WorkflowEntity workflow) { }

    /// <summary>
    /// Called when a connection is established involving this node.
    /// If the peer is an AiAgentNode, reads its agent_mode and reconfigures inputs
    /// from the runtime's PolicyLibrary.
    /// </summary>
    void IWorkflowNodeLifecycle.OnConnected(WorkflowConnection connection, WorkflowNodeDefinition peer, WorkflowEntity workflow)
    {
        if (!string.Equals(peer.NodeType, AgentNodeType, StringComparison.OrdinalIgnoreCase))
            return;

        string? modeId = peer.GetParameter(AgentModeParameter)?.GetString();
        if (string.IsNullOrWhiteSpace(modeId))
            return;

        IAgenticModeRuntime? runtime = null;
        foreach (IAgentEngine engine in _engines)
        {
            if (engine.TryGetModeRuntime(modeId, out IAgenticModeRuntime? r) && r is not null)
            {
                runtime = r;
                break;
            }
        }

        if (runtime is null)
            return;

        ReconfigureFromRuntime(runtime.PolicyLibrary);
    }

    // ----------------------------------------------------------------
    // IAgentPolicySource
    // ----------------------------------------------------------------

    AgentPolicyLayer? IAgentPolicySource.BuildPolicyLayer(ResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var policies = new AgentPolicySet(Conversation: BuildConversationPolicies(binding), Planning: BuildPlanningPolicies(binding), HumanLoop: BuildHumanLoopPolicies(binding), Workspace: BuildWorkspacePolicies(binding), TextualRules: BuildTextualRules(binding));

        return policies is { Conversation: null, Planning: null, HumanLoop: null, Workspace: null, TextualRules: null }
            ? null
            : new AgentPolicyLayer(
                Scope: ParseScope(binding.GetString("scope")),
                SourceId: string.IsNullOrWhiteSpace(binding.GetString("source_id")) ? binding.SourceNode.Id : binding.GetString("source_id")!.Trim(),
                Priority: binding.GetInt("priority") ?? AgentPolicyLayerFactory.ExplicitUserPolicyPriority,
                Policies: policies
            );
    }

    // ----------------------------------------------------------------
    // Reconfiguration
    // ----------------------------------------------------------------

    /// <summary>
    /// Removes legacy structured inputs and creates new inputs from the runtime's
    /// PolicyLibrary. Values for matching keys are preserved.
    /// textual_policy_tree and all PermanentInputs are never touched.
    /// </summary>
    private void ReconfigureFromRuntime(IReadOnlyList<RuntimePolicyDescriptor> library)
    {
        // Collect current default values before removing (key -> current default).
        var preserved = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (string? key in library.Select(d => d.Key))
        {
            object? current = GetInputDefaultValue(key);
            if (current is not null)
                preserved[key] = current;
        }

        // Remove all legacy prefixed inputs.
        RemoveInputsWhere(key => !PermanentInputs.Contains(key) && LegacyPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        // Remove any previously added runtime-dynamic inputs (not legacy, not permanent).
        RemoveInputsWhere(key => !PermanentInputs.Contains(key) && !LegacyPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        // Add new inputs from the runtime library.
        int order = 10;
        foreach (RuntimePolicyDescriptor descriptor in library)
        {
            _ = preserved.TryGetValue(descriptor.Key, out object? previousValue);
            object defaultValue = previousValue ?? descriptor.Default ?? "inherit";

            switch (descriptor.Kind)
            {
                case RuntimePolicyDescriptorKind.Boolean:
                    DefineChoice(
                        descriptor.Key,
                        BuildTriStateChoices(),
                        descriptor.Description,
                        defaultValue?.ToString() ?? "inherit",
                        ui: new ArgumentUiMetadata
                        {
                            Tab = "Structured policies",
                            Group = "Runtime",
                            Order = order,
                        }
                    );
                    break;

                case RuntimePolicyDescriptorKind.Text:
                    DefineInput(
                        descriptor.Key,
                        ArgumentType.Text,
                        descriptor.Description,
                        defaultValue,
                        ui: new ArgumentUiMetadata
                        {
                            Tab = "Structured policies",
                            Group = "Runtime",
                            Order = order,
                        }
                    );
                    break;

                case RuntimePolicyDescriptorKind.Choice:
                    // For Choice kind, descriptor.Default is expected to be a string.
                    DefineChoice(
                        descriptor.Key,
                        BuildTriStateChoices(),
                        descriptor.Description,
                        defaultValue?.ToString() ?? "inherit",
                        ui: new ArgumentUiMetadata
                        {
                            Tab = "Structured policies",
                            Group = "Runtime",
                            Order = order,
                        }
                    );
                    break;
            }

            order += 10;
        }
    }

    // ----------------------------------------------------------------
    // Policy builders (read dynamic inputs via binding key lookup)
    // ----------------------------------------------------------------

    private static AgentConversationPolicySet? BuildConversationPolicies(ResourceBinding binding)
    {
        bool? requireExplicitAlternatives = ParseOptionalBoolean(binding.GetString("conversation_require_explicit_alternatives_before_choice"));
        bool? disallowPrematureConvergence = ParseOptionalBoolean(binding.GetString("conversation_disallow_premature_convergence"));
        bool? requireDecisionContext = ParseOptionalBoolean(binding.GetString("conversation_require_decision_context_before_human_question"));
        return requireExplicitAlternatives.HasValue || disallowPrematureConvergence.HasValue || requireDecisionContext.HasValue ? new AgentConversationPolicySet(requireExplicitAlternatives, disallowPrematureConvergence, requireDecisionContext) : null;
    }

    private static AgentPlanningPolicySet? BuildPlanningPolicies(ResourceBinding binding)
    {
        bool? requireUnknowns = ParseOptionalBoolean(binding.GetString("planning_require_unknowns_to_be_represented"));
        bool? requireDecisionPoints = ParseOptionalBoolean(binding.GetString("planning_require_decision_points_when_alternatives_exist"));
        return requireUnknowns.HasValue || requireDecisionPoints.HasValue ? new AgentPlanningPolicySet(requireUnknowns, requireDecisionPoints) : null;
    }

    private static AgentHumanLoopPolicySet? BuildHumanLoopPolicies(ResourceBinding binding)
    {
        bool? askHumanOnlyForMaterialUnknowns = ParseOptionalBoolean(binding.GetString("human_loop_ask_human_only_for_material_unknowns"));
        bool? requireOptionsBeforeSelection = ParseOptionalBoolean(binding.GetString("human_loop_require_options_before_selection"));
        return askHumanOnlyForMaterialUnknowns.HasValue || requireOptionsBeforeSelection.HasValue ? new AgentHumanLoopPolicySet(askHumanOnlyForMaterialUnknowns, requireOptionsBeforeSelection) : null;
    }

    private static AgentWorkspacePolicySet? BuildWorkspacePolicies(ResourceBinding binding)
    {
        bool? storeOnlyContextualizedFacts = ParseOptionalBoolean(binding.GetString("agent_workspace_store_only_contextualized_facts"));
        bool? disallowAssistantSuggestionPromotion = ParseOptionalBoolean(binding.GetString("agent_workspace_disallow_assistant_suggestion_promotion"));
        bool? preserveOpenQuestions = ParseOptionalBoolean(binding.GetString("agent_workspace_preserve_open_questions"));
        return storeOnlyContextualizedFacts.HasValue || disallowAssistantSuggestionPromotion.HasValue || preserveOpenQuestions.HasValue ? new AgentWorkspacePolicySet(storeOnlyContextualizedFacts, disallowAssistantSuggestionPromotion, preserveOpenQuestions) : null;
    }

    private static IReadOnlyList<string>? BuildTextualRules(ResourceBinding binding)
    {
        JsonElement? structured = binding.GetStructured("textual_policy_tree");
        if (structured is not null)
        {
            var document = AgentTextualPolicyDocument.Parse(structured.Value);
            var flattenedRules = document.FlattenEnabledPolicies().Where(rule => !string.IsNullOrWhiteSpace(rule)).Select(rule => rule.Trim()).Distinct(StringComparer.Ordinal).ToList();
            return flattenedRules.Count == 0 ? null : flattenedRules;
        }
        return null;
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static AgentPolicyScope ParseScope(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            "platform" => AgentPolicyScope.Platform,
            "resource" => AgentPolicyScope.Resource,
            "agent" => AgentPolicyScope.Agent,
            "mode" => AgentPolicyScope.Mode,
            _ => AgentPolicyScope.Run,
        };

    private static bool? ParseOptionalBoolean(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };

    private static (string, string)[] BuildTriStateChoices() => [("inherit", "Inherit"), ("true", "Enabled"), ("false", "Disabled")];

    private static JsonElement BuildTextualPolicyTreeEditorDefinition()
    {
        const string json = """
            {
              "layout": "tree-grid",
              "rootCollection": "items",
              "columns": [
                { "key": "enabled", "type": "checkbox", "label": "", "width": 32 },
                { "key": "name",    "type": "text", "label": "Group / Policy", "flex": 1 },
                { "key": "policy",  "type": "text", "label": "Rule text", "flex": 2 }
              ]
            }
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
