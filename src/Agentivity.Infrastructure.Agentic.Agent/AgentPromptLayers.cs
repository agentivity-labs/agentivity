using Agentivity.Infrastructure.Agentic.Agent.Guardrails;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Builds the optional, guardrail-wrapped system-prompt layers ("&lt;execution_policy&gt;",
/// agent-workspace prompt) shared between the workflow path
/// (<see cref="Agentivity.Infrastructure.Workflow.Agentic.WorkflowAgentExecutionPreparationCoordinator"/>)
/// and the standalone path (<see cref="Agentivity.Infrastructure.Agentic.Composables.AgentEntityRunner"/>).
/// <para>
/// Both paths generate the RAW layer content via the same shared generators
/// (<see cref="AgentPrompting.BuildExecutionPolicySystemPrompt"/>, <see cref="AgentSessionCompaction.BuildAgentWorkspacePrompt"/>)
/// — the only thing that differs is HOW guardrails are applied to that content: the workflow
/// path threads a trace-aware delegate (NodeConfig/WorkflowRunServices/AgentTraceMetadata),
/// while the standalone path calls <c>AgentHost.ApplyGuardrails</c> directly. Rather than
/// forcing one shape onto the other (which would mean inventing fake workflow trace context
/// for the standalone path, or stripping tracing from the workflow path — both of which are
/// real behavioural changes the characterization tests would rightly flag), each call site
/// reduces its own guardrail mechanism down to this minimal seam:
/// <c>Func&lt;AgentGuardrailStage, string, string?&gt; applyGuardrail</c> — "apply guardrails
/// for this stage to this content, returning the (possibly transformed/blocked) result."
/// That seam is all the shared logic here needs.
/// </para>
/// </summary>
public static class AgentPromptLayers
{
    /// <summary>
    /// Builds the "&lt;execution_policy&gt;" system-prompt layer from the mode runtime's
    /// resolved execution-policy rules. Returns <see cref="string.Empty"/> when there are no
    /// rules — characterized as "no layer" by <c>AgentConversationBuilder.BuildInitialMessages</c>
    /// (it skips blank layers), and frozen by
    /// <c>AiNodeCharacterizationTests.AiNode_ExecuteAsync_BuildsInitialMessages_InExpectedLayerOrder</c>.
    /// </summary>
    public static async ValueTask<string> BuildExecutionPolicyPromptAsync(IReadOnlyList<string> executionPolicyRules, Func<AgentGuardrailStage, string, ValueTask<string?>> applyGuardrail)
    {
        ArgumentNullException.ThrowIfNull(executionPolicyRules);
        ArgumentNullException.ThrowIfNull(applyGuardrail);

        if (executionPolicyRules.Count == 0)
            return string.Empty;

        var content = AgentPrompting.BuildExecutionPolicySystemPrompt(executionPolicyRules);
        return await applyGuardrail(AgentGuardrailStage.ExecutionPrompt, content).ConfigureAwait(false) ?? string.Empty;
    }

    /// <summary>
    /// Builds the agent-workspace prompt layer from the session's compaction metadata.
    /// Always routed through guardrails (mirrors <c>WorkflowAgentExecutionPreparationCoordinator</c>'s
    /// <c>knowledgeBasePrompt</c> construction) — even an empty/null generated prompt is passed
    /// through (<c>allowNullOrEmptyContent: true</c>) so guardrails can inject content from scratch.
    /// </summary>
    public static async ValueTask<string> BuildAgentWorkspacePromptAsync(IReadOnlyDictionary<string, object?> sessionMetadata, Func<AgentGuardrailStage, string, ValueTask<string?>> applyGuardrail)
    {
        ArgumentNullException.ThrowIfNull(sessionMetadata);
        ArgumentNullException.ThrowIfNull(applyGuardrail);

        var generated = AgentSessionCompaction.BuildAgentWorkspacePrompt(sessionMetadata) ?? string.Empty;
        return await applyGuardrail(AgentGuardrailStage.AgentWorkspacePrompt, generated).ConfigureAwait(false) ?? string.Empty;
    }
}
