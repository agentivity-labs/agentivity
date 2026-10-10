using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Resources;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Standalone implementation of <see cref="IAgentGuardrailPort"/>.
/// Wraps pre-resolved <see cref="AgentGuardrailDefinition"/> list built from
/// <see cref="AgentEntity.Graph"/> bindings by <see cref="AgentGraphResolver"/>
/// and delegates execution to <see cref="AgentHost.ApplyGuardrailsAsync"/>.
/// <para>
/// <paramref name="llmProvider"/>/<paramref name="llmBinding"/> (optional) let an LlmJudge
/// guardrail reuse the agent's own connected LLM for classification — same pair
/// <see cref="AgentDefinitionResolver"/> already resolved to run the agent itself, just handed
/// down here too. When null (should not happen on the standalone/team path, which always
/// resolves an LLM model node first), LlmJudge guardrails fail open.
/// </para>
/// </summary>
internal sealed class ComposedAgentGuardrailProxy : IAgentGuardrailPort
{
    private readonly AgentHost _agentHost;
    private readonly IReadOnlyList<AgentGuardrailDefinition> _guardrails;
    private readonly ILlmProvider? _llmProvider;
    private readonly ResourceBinding? _llmBinding;

    public ComposedAgentGuardrailProxy(AgentHost agentHost, IReadOnlyList<AgentGuardrailDefinition> guardrails, ILlmProvider? llmProvider = null, ResourceBinding? llmBinding = null)
    {
        _agentHost = agentHost ?? throw new ArgumentNullException(nameof(agentHost));
        _guardrails = guardrails ?? throw new ArgumentNullException(nameof(guardrails));
        _llmProvider = llmProvider;
        _llmBinding = llmBinding;
    }

    public async ValueTask<string> ApplyAsync(string stage, string content, List<AgentGuardrailMatch>? matchSink = null, CancellationToken cancellationToken = default)
    {
        var guardrailStage = MapStage(stage);
        if (guardrailStage is null)
        {
            return content;
        }

        Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<System.Text.Json.Nodes.JsonObject>?, LlmConversationOptions, CancellationToken, ValueTask<LlmConversationResponse>>? invokeLlm = _llmProvider is not null && _llmBinding is not null ? InvokeLlmAsync : null;
        return await _agentHost.ApplyGuardrailsAsync(_guardrails, guardrailStage.Value, content, allowNullOrEmptyContent: false, invokeLlm, cancellationToken, matchSink).ConfigureAwait(false) ?? content;
    }

    private async ValueTask<LlmConversationResponse> InvokeLlmAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<System.Text.Json.Nodes.JsonObject>? tools, LlmConversationOptions options, CancellationToken cancellationToken)
    {
        var result = await _llmProvider!.CompleteConversationAsync(_llmBinding!, messages, tools, options, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure || result.Value is null)
        {
            throw new InvalidOperationException(result.Error ?? "LLM judge guardrail call failed.");
        }

        return result.Value;
    }

    private static AgentGuardrailStage? MapStage(string stage) =>
        stage switch
        {
            AgentGuardrailContext.ExecutionPrompt => AgentGuardrailStage.ExecutionPrompt,
            AgentGuardrailContext.TaskBatchPrompt => AgentGuardrailStage.TaskBatchPrompt,
            AgentGuardrailContext.JoinPrompt => AgentGuardrailStage.JoinPrompt,
            AgentGuardrailContext.ReplanningPrompt => AgentGuardrailStage.ReplanningPrompt,
            AgentGuardrailContext.FinalResponse => AgentGuardrailStage.FinalResponse,
            AgentGuardrailContext.HumanLoopPrompt => AgentGuardrailStage.HumanLoopPrompt,
            AgentGuardrailContext.ResponseQualityAssessmentPrompt => AgentGuardrailStage.ResponseQualityAssessmentPrompt,
            _ => null,
        };
}
