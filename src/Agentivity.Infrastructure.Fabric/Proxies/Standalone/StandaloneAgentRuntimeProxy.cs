using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Execution;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Standalone implementation of <see cref="IAgentLlmProxy"/>.
/// Dispatches LLM calls via the <see cref="ILlmProvider"/> resource node connected to the agent's <c>llm_model</c> port.
/// Tool dispatch is handled by the agent graph resource connections (STORY-0437.4).
/// <para>
/// When <paramref name="telemetryEmitter"/> and <paramref name="correlation"/> are supplied, each LLM call
/// emits <see cref="LlmCallStartedEvent"/>/<see cref="LlmCallCompletedEvent"/> on the
/// <see cref="IAgentTelemetryEmitter"/> — mirroring <c>WorkflowAgentLlmCoordinator.InvokeLlmConversationAsync</c>
/// (the AiNode path). This is monitoring telemetry only and is independent of the chat message
/// stream — the assistant's response text reaches the client via a separate
/// <c>RunEventKind.message_sent</c> event published by <c>AgentExecutable.ExecuteAgentAsync</c>.
/// </para>
/// </summary>
internal sealed class StandaloneAgentRuntimeProxy(ILlmProvider llmProvider, ResourceBinding llmBinding, StandaloneAgentToolProxy? toolPort = null, Action<string>? publishStatus = null, IAgentTelemetryEmitter? telemetryEmitter = null, ExecutionCorrelationContext? correlation = null) : IAgentLlmProxy
{
    public async ValueTask<LlmConversationResponse> InvokeLlmConversationAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? toolDefs, LlmConversationOptions? options, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var operationKind = toolDefs is { Count: > 0 } ? "tool_enabled_conversation" : "conversation";
        var provider = llmBinding.GetString("channel_type") ?? llmBinding.SourceNode.NodeType ?? llmBinding.SourceNodeId;
        var model = llmBinding.GetString("model") ?? llmBinding.GetString("deployment") ?? llmBinding.SourceNodeId;

        await LlmCallTelemetryEmitter.EmitStartedAsync(telemetryEmitter, correlation, provider, model, operationKind, startedAtUtc, options?.ToolChoiceMode.ToString(), cancellationToken).ConfigureAwait(false);

        var result = await llmProvider.CompleteConversationAsync(llmBinding, messages, toolDefs, options, cancellationToken).ConfigureAwait(false);

        var completedAtUtc = DateTime.UtcNow;

        if (result.IsFailure || result.Value is null)
        {
            var errorMessage = result.Error ?? "LLM conversation failed.";
            await LlmCallTelemetryEmitter.EmitFailedAsync(telemetryEmitter, correlation, provider, model, operationKind, startedAtUtc, completedAtUtc, errorMessage, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(errorMessage);
        }

        var response = result.Value;
        await LlmCallTelemetryEmitter.EmitCompletedAsync(telemetryEmitter, correlation, provider, model, operationKind, startedAtUtc, completedAtUtc, response, cancellationToken).ConfigureAwait(false);

        return response;
    }

    public async ValueTask<AgentToolExecution> ExecuteToolCallAsync(SessionContext sessionContext, IReadOnlySet<string> reusableToolNames, LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);
        ArgumentNullException.ThrowIfNull(reusableToolNames);
        ArgumentNullException.ThrowIfNull(toolCall);

        if (toolPort is null)
        {
            var notConnectedMessage = $"Tool '{toolCall.Name}' is not connected to this agent.";
            return new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, notConnectedMessage, true, false);
        }

        JsonElement arguments;
        try
        {
            using var argumentsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
            arguments = argumentsDocument.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            var invalidArgumentsMessage = $"Invalid JSON arguments for tool '{toolCall.Name}': {ex.Message}";
            return new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, invalidArgumentsMessage, true, false);
        }

        if (SessionToolStateHelper.TryReuseSessionToolState(sessionContext, reusableToolNames, toolCall, out var reusedResultContent))
        {
            publishStatus?.Invoke($"Reusing AI agent tool '{toolCall.Name}' result from session tool state...");
            return new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, reusedResultContent, false, true);
        }

        publishStatus?.Invoke($"Executing AI agent tool '{toolCall.Name}'...");
        var result = await toolPort.CallToolAsync(toolCall.Name, arguments, cancellationToken).ConfigureAwait(false);

        var resultContent = McpToolResultJson.GetTextContent(result);
        var isError = McpToolResultJson.IsError(result);
        var failedReason = isError ? McpToolResultJson.GetFailedReason(result) : null;
        var debug = McpToolResultJson.GetDebug(result);
        SessionToolStateHelper.UpdateSessionToolState(sessionContext, toolCall, resultContent, isError);

        return new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, resultContent, isError, false, debug, failedReason);
    }
}
