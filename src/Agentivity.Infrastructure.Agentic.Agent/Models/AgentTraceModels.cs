using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed record AgentTraceMetadata(
    string Prompt,
    IReadOnlyList<string> ToolNames,
    string AgentMode,
    string? ToolChoiceMode,
    bool AllowParallelToolCalls,
    bool StructuredResponseEnabled,
    bool HumanLoopEnabled,
    bool SessionEnabled,
    double Temperature,
    int? MaxTokens,
    int? MaxIterations,
    double? ResponseQualityThreshold,
    double? PlanQualityThreshold,
    AgentTraceBudgetState BudgetState,
    ResolvedAgentPolicies ResolvedPolicies,
    IReadOnlyList<AgentGuardrailDefinition> ResolvedGuardrails
);

public sealed class AgentTraceBudgetState
{
    public int LlmCallCount { get; private set; }

    public int TotalTokensUsed { get; private set; }

    public void Register(LlmConversationResponse response)
    {
        LlmCallCount++;
        TotalTokensUsed += Math.Max(0, response.TokensUsed);
    }
}

public sealed record AgentTraceEvent(
    int Iteration,
    string FinishReason,
    string? AssistantContent,
    List<AgentToolExecution> ToolCalls,
    IReadOnlyList<string>? ScheduledTaskIds = null,
    int? OutputStateCount = null,
    int? ArtifactTaskCount = null,
    ResolvedAgentPolicies? Policies = null,
    IReadOnlyList<AgentGuardrailDefinition>? Guardrails = null
) : TraceEvent(Iteration)
{
    public override DebugTraceItem ToDebugTraceItem()
    {
        var fields = new List<DebugTraceField> { new("Iteration", Iteration.ToString()), new("Tool calls", ToolCalls.Count.ToString()) };

        if (ScheduledTaskIds is { Count: > 0 })
            fields.Add(new DebugTraceField("Scheduled tasks", string.Join(", ", ScheduledTaskIds)));

        if (OutputStateCount.HasValue)
            fields.Add(new DebugTraceField("Output state count", OutputStateCount.Value.ToString()));

        if (ArtifactTaskCount.HasValue)
            fields.Add(new DebugTraceField("Artifact task count", ArtifactTaskCount.Value.ToString()));

        if (Policies is not null)
            fields.Add(new DebugTraceField("Resolved policies", Policies.Entries.Count.ToString()));

        if (Guardrails is not null)
            fields.Add(new DebugTraceField("Resolved guardrails", Guardrails.Count.ToString()));

        if (ToolCalls.Count > 0)
        {
            fields.Add(new DebugTraceField("Tool names", string.Join(", ", ToolCalls.Select(tc => tc.ToolName))));

            if (ToolCalls.Any(tc => !string.IsNullOrWhiteSpace(tc.ArgumentsJson)))
            {
                fields.Add(new DebugTraceField("Tool arguments", string.Join(Environment.NewLine + Environment.NewLine, ToolCalls.Where(tc => !string.IsNullOrWhiteSpace(tc.ArgumentsJson)).Select(tc => $"{tc.ToolName}: {tc.ArgumentsJson}"))));
            }
        }

        var children = new List<DebugTraceItem>();
        foreach (var toolCall in ToolCalls)
        {
            var toolFields = new List<DebugTraceField> { new("Tool name", toolCall.ToolName), new("Tool call id", toolCall.ToolCallId), new("Reused from state", toolCall.ReusedFromToolState.ToString()) };

            if (!string.IsNullOrWhiteSpace(toolCall.ArgumentsJson))
                toolFields.Add(new DebugTraceField("Arguments", toolCall.ArgumentsJson));

            if (!string.IsNullOrWhiteSpace(toolCall.ResultContent))
                toolFields.Add(new DebugTraceField("Result", toolCall.ResultContent));

            var toolChildren = new List<DebugTraceItem>();
            if (toolCall.Debug is not null)
                toolChildren.Add(toolCall.Debug);

            children.Add(
                new DebugTraceItem(
                    Kind: toolCall.IsError ? "tool_error" : "tool_call",
                    Name: toolCall.ToolName,
                    Message: toolCall.IsError ? "Tool call failed." : "Tool call executed.",
                    Status: toolCall.IsError ? DebugTraceStatus.Error : DebugTraceStatus.Info,
                    Fields: toolFields,
                    Children: toolChildren.Count == 0 ? null : toolChildren
                )
            );
        }

        var status =
            FinishReason.Contains("failed", StringComparison.OrdinalIgnoreCase) || FinishReason.Contains("error", StringComparison.OrdinalIgnoreCase) ? DebugTraceStatus.Error
            : FinishReason.Contains("blocked", StringComparison.OrdinalIgnoreCase) || FinishReason.Contains("replan", StringComparison.OrdinalIgnoreCase) ? DebugTraceStatus.Warning
            : DebugTraceStatus.Info;

        return new DebugTraceItem(Kind: FinishReason, Name: FinishReason.Replace('_', ' '), Message: string.IsNullOrWhiteSpace(AssistantContent) ? null : AssistantContent, Status: status, Fields: fields, Children: children.Count == 0 ? null : children);
    }
}

public sealed record AgentTraceAssessment(
    string Kind,
    double FinalScore,
    List<AgentTraceAssessmentDimension> DimensionScores,
    List<string> Strengths,
    List<string> ImprovementAreas,
    string RefinementGuideline,
    string? Decision = null,
    string? PrimaryTrigger = null,
    List<string>? ReplanTriggers = null,
    string? BlockingReason = null
);

public sealed record AgentTraceAssessmentDimension(string Name, double Score);
