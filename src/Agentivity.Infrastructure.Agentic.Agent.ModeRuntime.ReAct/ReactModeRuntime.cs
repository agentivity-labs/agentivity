using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.ModeRuntime.ReAct;

/// <summary>
/// ReAct runtime — implements the Thought/Action/Observation loop (Yao et al. 2022).
///
/// Adopted extensions (see architecture/decisions/):
/// - Sequential tool execution only: ADR-001
/// - In-loop scratchpad compaction: ADR-002
/// - Observation sanitization (prompt injection defense): ADR-003
/// </summary>
public sealed class ReactModeRuntime()
    : AgenticModeRuntimeBase(
        ModeIdValue,
        "ReAct",
        capabilities: new AgenticModeCapabilities(UsesConnectedTools: true, AllowsToolChoiceConfiguration: true, SanitizesObservations: true, EmitsReActStepTrace: true),
        defaultPolicies: new AgentPolicySet(HumanLoop: new AgentHumanLoopPolicySet(AskHumanOnlyForMaterialUnknowns: true))
    )
{
    public const string ModeIdValue = "react";

    public override string? Description => "Thinks, acts with a tool, looks at the result, and repeats until it can answer.";

    public override string? WhenToUse => "Open-ended tasks where the next step depends on what was just found, such as research, investigation or troubleshooting. Slower and more costly than tools.";

    public override async ValueTask<AgentRunResult> ExecuteAsync(AgentConfig config, AgentRunState state, AgentRunServices services, AgentHost agentHost, CancellationToken cancellationToken)
    {
        var manifest = BuildManifest(agentHost, config, state, services, state.ResumeState);

        try
        {
            return await RunCoreAsync(manifest, config, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentDegradedException ex)
        {
            manifest.AppendTrace(new AgentTraceEvent(0, "react_degraded", ex.Message, []));
            manifest.PublishStatusMessage($"The agent could not complete the request ({ex.FinishReason}): {ex.UserFacingMessage}");
            return AgentRunResult.Degraded(ex.FinishReason, ex.UserFacingMessage);
        }
    }

    private async ValueTask<AgentRunResult> RunCoreAsync(IAgentManifest manifest, AgentConfig config, CancellationToken cancellationToken)
    {
        // Inject ReAct system prompt once.
        var policyRules = manifest.GetPolicyRules(AgentPolicyContext.Execution);
        if (HumanLoopHelper.SupportsFreeTextHumanLoop(config.EnableHumanLoop, config.HumanLoopToolDefinitionsByName))
            policyRules = [.. policyRules, .. manifest.GetPolicyRules(AgentPolicyContext.HumanLoop)];
        var systemPrompt = ReactPromptBuilder.BuildSystemPrompt(policyRules);
        systemPrompt = await manifest.ApplyGuardrailsAsync(AgentGuardrailContext.ExecutionPrompt, systemPrompt, cancellationToken).ConfigureAwait(false);
        manifest.EnsureSystemPrompt(systemPrompt);
        manifest.AppendTrace(new AgentTraceEvent(0, "react_runtime_prepared", systemPrompt, []));

        // Resume: replay pending tool calls from a prior human-review suspension.
        var resume = manifest.ResumeState;
        if (resume.PendingToolCalls.Count > 0)
        {
            var resumedCalls = resume.PendingToolCalls.Select(p => new LlmToolCall(p.Id, p.Name, p.ArgumentsJson)).ToList();
            await manifest.ExecuteToolsAsync(resumedCalls, 0, cancellationToken).ConfigureAwait(false);
        }

        var maxIter = manifest.MaxIterations ?? int.MaxValue;

        for (var i = resume.IterationsCompleted + 1; i <= maxIter; i++)
        {
            await manifest.PersistAgentStateAsync(cancellationToken).ConfigureAwait(false);
            await manifest.RunSummaryIfDueAsync(cancellationToken).ConfigureAwait(false);

            var response = await manifest.RunMainTurnAsync(manifest.ToolDefinitions, allowParallelToolCalls: false, cancellationToken).ConfigureAwait(false);
            response = await manifest.ResolveTurnConclusionAsync(response, i, cancellationToken).ConfigureAwait(false);

            manifest.AppendTrace(
                response.ToolCalls.Count > 0
                    ? new AgentTraceEvent(i, "react_action", response.Content, [.. response.ToolCalls.Select(tc => new AgentToolExecution(tc.Id, tc.Name, tc.ArgumentsJson, string.Empty, false, false))])
                    : new AgentTraceEvent(i, "react_thought", response.Content, [])
            );

            var result = await manifest.TryFinalizeTurnAsync(response, i, cancellationToken).ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        return AgentRunResult.MaxIterations(manifest.MaxIterations ?? 0);
    }
}
