using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.ModeRuntime.Native;

public sealed class ConversationalModeRuntime() : AgenticModeRuntimeBase(ModeIdValue, "Conversational")
{
    public const string ModeIdValue = "conversational";

    public override string? Description => "Answers directly from the conversation and its context, without using any tool.";

    public override string? WhenToUse => "Chat, writing, explanations and classification, where no lookup or outside action is needed.";

    public override async ValueTask<AgentRunResult> ExecuteAsync(AgentConfig config, AgentRunState state, AgentRunServices services, AgentHost agentHost, CancellationToken cancellationToken)
    {
        var manifest = BuildManifest(agentHost, config, state, services, state.ResumeState);
        ArgumentNullException.ThrowIfNull(manifest);

        var resume = manifest.ResumeState;
        if (resume.PendingToolCalls.Count > 0)
        {
            var resumedCalls = resume.PendingToolCalls.Select(p => new LlmToolCall(p.Id, p.Name, p.ArgumentsJson)).ToList();
            await manifest.ExecuteToolsAsync(resumedCalls, 0, cancellationToken).ConfigureAwait(false);
        }

        var maxIter = manifest.MaxIterations ?? int.MaxValue;
        if (HumanLoopHelper.SupportsFreeTextHumanLoop(config.EnableHumanLoop, config.HumanLoopToolDefinitionsByName))
        {
            var humanLoopRules = manifest.GetPolicyRules(AgentPolicyContext.HumanLoop);
            if (humanLoopRules.Count > 0)
                manifest.EnsureSystemPrompt(string.Join(Environment.NewLine, humanLoopRules));
        }

        for (var i = resume.IterationsCompleted + 1; i <= maxIter; i++)
        {
            await manifest.PersistAgentStateAsync(cancellationToken).ConfigureAwait(false);
            await manifest.RunSummaryIfDueAsync(cancellationToken).ConfigureAwait(false);

            var tools = manifest.ToolDefinitions.Count > 0 ? manifest.ToolDefinitions : null;
            var response = await manifest.RunMainTurnAsync(tools, allowParallelToolCalls: false, cancellationToken).ConfigureAwait(false);
            response = await manifest.ResolveTurnConclusionAsync(response, i, cancellationToken).ConfigureAwait(false);

            var result = await manifest.TryFinalizeTurnAsync(response, i, cancellationToken).ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        return AgentRunResult.MaxIterations(manifest.MaxIterations ?? 0);
    }
}
