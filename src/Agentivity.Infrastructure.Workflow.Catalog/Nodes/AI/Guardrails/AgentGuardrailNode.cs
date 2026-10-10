using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Guardrails;

public sealed class AgentGuardrailNode : ResourceNode, IAgentGuardrailSource
{
    public AgentGuardrailNode()
        : base("ai.guardrails.keyword")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentGuardrail);

        DefineMetadata("AI Agent Guardrail", "Produces a simple keyword-based guardrail that can warn, rewrite, or block selected AI-agent runtime surfaces.", "ai.guardrails");

        ClearInputPorts();
        ClearOutputPorts();
        DefineResourceOutputPort("resource", "AI Agent Guardrail", ResourcePortCategory.AIAgentGuardrail, maxConnections: -1);

        DefineChoice(
            "stage",
            [
                ("planning_prompt", "Planning Prompt"),
                ("execution_prompt", "Execution Prompt"),
                ("task_batch_prompt", "Task Batch Prompt"),
                ("join_prompt", "Join Prompt"),
                ("replanning_prompt", "Replanning Prompt"),
                ("plan_quality_assessment_prompt", "Plan Quality Assessment Prompt"),
                ("response_quality_assessment_prompt", "Response Quality Assessment Prompt"),
                ("response_quality_refinement_prompt", "Response Quality Refinement Prompt"),
                ("tool_failure_recovery_prompt", "Tool Failure Recovery Prompt"),
                ("human_loop_duplicate_prompt", "Human Loop Duplicate Prompt"),
                ("structured_response_finalization_prompt", "Structured Response Finalization Prompt"),
                ("conversational_finalization_prompt", "Conversational Finalization Prompt"),
                ("agent_workspace_consolidation_prompt", "Knowledge Base Consolidation Prompt"),
                ("human_loop_prompt", "Human Loop Prompt"),
                ("agent_workspace_prompt", "Knowledge Base Prompt"),
                ("final_response", "Final Response"),
            ],
            "Guardrail stage",
            "final_response"
        );
        DefineChoice("action", [("warn", "Warn"), ("block", "Block"), ("rewrite", "Rewrite")], "Guardrail action", "warn");
        DefineInput("priority", ArgumentType.Number, "Evaluation priority (lower first)", 1000);
        DefineInput("guardrail_id", ArgumentType.Text, "Optional guardrail identifier", "");
        DefineInput("match_text", ArgumentType.Text, "Literal text to detect", "");
        DefineInput("message", ArgumentType.Text, "Guardrail message", "");
        DefineInput("replacement_text", ArgumentType.Text, "Replacement text used when action is rewrite", "");
        DefineInput("ignore_case", ArgumentType.Boolean, "Ignore case while matching", true);

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    AgentGuardrailDefinition? IAgentGuardrailSource.BuildGuardrail(ResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        string? matchText = binding.GetString("match_text")?.Trim();
        if (string.IsNullOrWhiteSpace(matchText))
        {
            return null;
        }

        string? guardrailId = binding.GetString("guardrail_id");
        return new AgentGuardrailDefinition(
            GuardrailId: string.IsNullOrWhiteSpace(guardrailId) ? binding.SourceNode.Id : guardrailId.Trim(),
            Stage: ParseStage(binding.GetString("stage")),
            Action: ParseAction(binding.GetString("action")),
            Priority: binding.GetInt("priority") ?? 1000,
            MatchText: matchText,
            Message: binding.GetString("message")?.Trim() ?? string.Empty,
            ReplacementText: binding.GetString("replacement_text"),
            IgnoreCase: binding.GetBool("ignore_case") ?? true
        );
    }

    private static AgentGuardrailStage ParseStage(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            "planning_prompt" => AgentGuardrailStage.PlanningPrompt,
            "execution_prompt" => AgentGuardrailStage.ExecutionPrompt,
            "task_batch_prompt" => AgentGuardrailStage.TaskBatchPrompt,
            "join_prompt" => AgentGuardrailStage.JoinPrompt,
            "replanning_prompt" => AgentGuardrailStage.ReplanningPrompt,
            "plan_quality_assessment_prompt" => AgentGuardrailStage.PlanQualityAssessmentPrompt,
            "response_quality_assessment_prompt" => AgentGuardrailStage.ResponseQualityAssessmentPrompt,
            "response_quality_refinement_prompt" => AgentGuardrailStage.ResponseQualityRefinementPrompt,
            "tool_failure_recovery_prompt" => AgentGuardrailStage.ToolFailureRecoveryPrompt,
            "human_loop_duplicate_prompt" => AgentGuardrailStage.HumanLoopDuplicatePrompt,
            "structured_response_finalization_prompt" => AgentGuardrailStage.StructuredResponseFinalizationPrompt,
            "conversational_finalization_prompt" => AgentGuardrailStage.ConversationalFinalizationPrompt,
            "agent_workspace_consolidation_prompt" => AgentGuardrailStage.AgentWorkspaceConsolidationPrompt,
            "human_loop_prompt" => AgentGuardrailStage.HumanLoopPrompt,
            "agent_workspace_prompt" => AgentGuardrailStage.AgentWorkspacePrompt,
            _ => AgentGuardrailStage.FinalResponse,
        };

    private static AgentGuardrailAction ParseAction(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            "block" => AgentGuardrailAction.Block,
            "rewrite" => AgentGuardrailAction.Rewrite,
            _ => AgentGuardrailAction.Warn,
        };
}
