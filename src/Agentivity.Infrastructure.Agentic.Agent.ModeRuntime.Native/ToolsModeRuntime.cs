using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.ModeRuntime.Native;

public sealed class ToolsModeRuntime() : AgenticModeRuntimeBase(ModeIdValue, "Tools", capabilities: new AgenticModeCapabilities(UsesConnectedTools: true, AllowsToolChoiceConfiguration: true))
{
    public const string ModeIdValue = "tools";

    public override string? Description => "Answers and may call the connected tools in a single pass, choosing which tool to use.";

    public override string? WhenToUse => "Simple tasks with a few tools, such as looking something up, reading a record or sending one message. Not for multi-step work that needs planning.";

    public override async ValueTask<AgentRunResult> ExecuteAsync(AgentConfig config, AgentRunState state, AgentRunServices services, AgentHost agentHost, CancellationToken cancellationToken)
    {
        var infra = BuildManifest(agentHost, config, state, services, state.ResumeState);
        ArgumentNullException.ThrowIfNull(infra);

        var resume = infra.ResumeState;
        if (resume.PendingToolCalls.Count > 0)
        {
            var resumedCalls = resume.PendingToolCalls.Select(p => new LlmToolCall(p.Id, p.Name, p.ArgumentsJson)).ToList();
            await infra.ExecuteToolsAsync(resumedCalls, 0, cancellationToken).ConfigureAwait(false);
        }

        var maxIter = infra.MaxIterations ?? int.MaxValue;
        if (HumanLoopHelper.SupportsFreeTextHumanLoop(config.EnableHumanLoop, config.HumanLoopToolDefinitionsByName))
        {
            var humanLoopRules = infra.GetPolicyRules(AgentPolicyContext.HumanLoop);
            if (humanLoopRules.Count > 0)
                infra.EnsureSystemPrompt(string.Join(Environment.NewLine, humanLoopRules));
        }

        for (var i = resume.IterationsCompleted + 1; i <= maxIter; i++)
        {
            await infra.PersistAgentStateAsync(cancellationToken).ConfigureAwait(false);
            await infra.RunSummaryIfDueAsync(cancellationToken).ConfigureAwait(false);

            var response = await infra.RunMainTurnAsync(infra.ToolDefinitions, infra.AllowParallelToolCalls, cancellationToken).ConfigureAwait(false);
            response = await infra.ResolveTurnConclusionAsync(response, i, cancellationToken).ConfigureAwait(false);

            var result = await infra.TryFinalizeTurnAsync(response, i, cancellationToken).ConfigureAwait(false);
            if (result is not null)
                return result;
        }

        return AgentRunResult.MaxIterations(infra.MaxIterations ?? 0);
    }
}
