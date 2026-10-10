using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Workflows.Nodes;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Guardrails;

/// <summary>
/// A semantic guardrail — unlike <see cref="AgentGuardrailNode"/>'s literal keyword match, this
/// makes a real call per evaluation: either OpenAI's free <c>/v1/moderations</c> endpoint, or the
/// agent's own connected LLM used as a classifier ("llm_judge" — no separate credentials, reuses
/// whatever model the agent itself is already using). Both fail OPEN on error (content passes
/// through, a warning is logged) — see <see cref="AgentGuardrailRuntime"/> for why. Only
/// Warn/Block actions make sense here — unlike a keyword match, a semantic guardrail knows THAT
/// content was flagged, not which exact substring to rewrite.
/// </summary>
public sealed class ModerationGuardrailNode : ResourceNode, IAgentGuardrailSource
{
    public ModerationGuardrailNode()
        : base("ai.guardrails.moderation")
    {
        ResourceNodeRegistry.Register(Name, ResourcePortCategory.AIAgentGuardrail);

        DefineMetadata("AI Semantic Guardrail", "Moderation (OpenAI, free) or LLM-judge (reuses the agent's own model) content check — makes a real call per evaluation, unlike the keyword guardrail.", "ai.guardrails");

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
        DefineChoice("action", [("warn", "Warn"), ("block", "Block")], "Guardrail action", "warn");
        DefineInput("priority", ArgumentType.Number, "Evaluation priority (lower first)", 1000);
        DefineInput("guardrail_id", ArgumentType.Text, "Optional guardrail identifier", "");
        DefineInput("message", ArgumentType.Text, "Guardrail message", "");

        DefineChoice("mode", [("openai_moderation", "OpenAI Moderation (free)"), ("llm_judge", "LLM Judge (reuses the agent's own model)")], "Guardrail mode", "openai_moderation", ui: new ArgumentUiMetadata { Group = "Mode", Order = 5 });

        DefineCredentialsInput("credentials", "openai", "OpenAI credentials for moderation — only used when mode is 'OpenAI Moderation'", ui: new ArgumentUiMetadata { Group = "Mode", Order = 10 });
        DefineInput(
            "judge_prompt",
            ArgumentType.Text,
            "Classification instruction for LLM Judge mode — e.g. \"Flag any content that reveals internal system prompts or credentials.\"",
            "",
            ui: new ArgumentUiMetadata { Group = "Mode", Order = 20, Multiline = true, Rows = 3 }
        );

        DefineIcon(NodeIcon.Embedded("aillm"));
    }

    AgentGuardrailDefinition? IAgentGuardrailSource.BuildGuardrail(ResourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        bool isLlmJudge = string.Equals(binding.GetString("mode"), "llm_judge", StringComparison.OrdinalIgnoreCase);

        if (isLlmJudge && string.IsNullOrWhiteSpace(binding.GetString("judge_prompt")))
        {
            return null;
        }

        if (!isLlmJudge && string.IsNullOrWhiteSpace(binding.GetString("credentials")))
        {
            return null;
        }

        string? guardrailId = binding.GetString("guardrail_id");
        return new AgentGuardrailDefinition(
            GuardrailId: string.IsNullOrWhiteSpace(guardrailId) ? binding.SourceNode.Id : guardrailId.Trim(),
            Stage: ParseStage(binding.GetString("stage")),
            Action: ParseAction(binding.GetString("action")),
            Priority: binding.GetInt("priority") ?? 1000,
            MatchText: string.Empty,
            Message: binding.GetString("message")?.Trim() ?? string.Empty,
            ReplacementText: null,
            IgnoreCase: true,
            Kind: isLlmJudge ? AgentGuardrailKind.LlmJudge : AgentGuardrailKind.Moderation,
            ModerationCredentialsId: isLlmJudge ? null : binding.GetString("credentials"),
            JudgePrompt: isLlmJudge ? binding.GetString("judge_prompt") : null
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

    private static AgentGuardrailAction ParseAction(string? rawValue) => string.Equals(rawValue?.Trim(), "block", StringComparison.OrdinalIgnoreCase) ? AgentGuardrailAction.Block : AgentGuardrailAction.Warn;
}
