using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Common;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Agentic.Agent.Execution;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events.Agent;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Workflow implementation of <see cref="IAgentLlmProxy"/>.
/// Dispatches LLM calls via the <c>llm_model</c> resource and tool calls via MCP,
/// emitting <see cref="LlmCallTelemetryEmitter"/> events on the way — mirroring
/// <c>StandaloneAgentExecutionRuntimePort</c> (the standalone path).
/// </summary>
public sealed class WorkflowAgentLlmProxy(NodeConfig config, WorkflowRunServices services) : IAgentLlmProxy
{
    private NodeConfig Config { get; } = config ?? throw new ArgumentNullException(nameof(config));
    private WorkflowRunServices Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    public async ValueTask<LlmConversationResponse> InvokeLlmConversationAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("At least one conversation message is required.", nameof(messages));
        }

        const string resourcePortName = "llm_model";
        var (llm, binding) = ResourceResolver.RequireToolNode<ILlmProvider>(Config, resourcePortName, ResourcePortCategory.LlmModel);
        var startedAtUtc = DateTime.UtcNow;
        var operationKind = InferLlmOperationKind(messages, options, tools);
        var spanId = $"llm-{Guid.NewGuid():N}";

        await LlmCallTelemetryEmitter
            .EmitStartedAsync(Services.TelemetryEmitter, BuildLlmCorrelationContext(Config, Services, operationKind, spanId), ResolveLlmProvider(binding), ResolveLlmModel(binding), operationKind, startedAtUtc, options?.ToolChoiceMode.ToString(), cancellationToken)
            .ConfigureAwait(false);

        OperationResult<LlmConversationResponse> result;
        try
        {
            result = await llm.CompleteConversationAsync(binding, messages, tools, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LlmCallTelemetryEmitter
                .EmitFailedAsync(Services.TelemetryEmitter, BuildLlmCorrelationContext(Config, Services, operationKind, spanId), ResolveLlmProvider(binding), ResolveLlmModel(binding), operationKind, startedAtUtc, DateTime.UtcNow, ex.Message, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        if (result.IsFailure || result.Value is null)
        {
            var errorMessage = result.Error ?? "LLM conversation failed.";
            Services.PublishStatus($"LLM call failed: {errorMessage}");
            await LlmCallTelemetryEmitter
                .EmitFailedAsync(Services.TelemetryEmitter, BuildLlmCorrelationContext(Config, Services, operationKind, spanId), ResolveLlmProvider(binding), ResolveLlmModel(binding), operationKind, startedAtUtc, DateTime.UtcNow, errorMessage, cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(errorMessage);
        }

        var response = result.Value;
        await LlmCallTelemetryEmitter
            .EmitCompletedAsync(Services.TelemetryEmitter, BuildLlmCorrelationContext(Config, Services, operationKind, spanId), ResolveLlmProvider(binding), ResolveLlmModel(binding), operationKind, startedAtUtc, DateTime.UtcNow, response, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            Services.PublishStatus($"LLM response truncated (FinishReason=length). TokensUsed={response.TokensUsed}, ContentLength={response.Content?.Length ?? 0}, RequestedMaxTokens={options?.MaxTokens?.ToString() ?? "default"}. The model ran out of output tokens before completing its response.");
        }

        if (TryGetTraceBudgetState(Config, out var budgetState))
        {
            budgetState.Register(response);
            RefreshTraceBudgetFields(Config, Services);
        }

        return response;
    }

    public async ValueTask<AgentToolExecution> ExecuteToolCallAsync(SessionContext sessionContext, IReadOnlySet<string> reusableToolNames, LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);
        ArgumentNullException.ThrowIfNull(reusableToolNames);
        ArgumentNullException.ThrowIfNull(toolCall);

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
            Services.PublishStatus($"Reusing AI agent tool '{toolCall.Name}' result from session tool state...");
            return new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, reusedResultContent, false, true);
        }

        Services.PublishStatus($"Executing AI agent tool '{toolCall.Name}'...");
        var result = await McpToolRouter.CallToolAsync(Config, AgentToolPorts.All, toolCall.Name, arguments, cancellationToken).ConfigureAwait(false);

        var resultContent = McpToolResultJson.GetTextContent(result);
        var isError = McpToolResultJson.IsError(result);
        var failedReason = isError ? McpToolResultJson.GetFailedReason(result) : null;
        var debug = McpToolResultJson.GetDebug(result);
        SessionToolStateHelper.UpdateSessionToolState(sessionContext, toolCall, resultContent, isError);

        return new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, resultContent, isError, false, debug, failedReason);
    }

    // ── LLM call helpers (telemetry, operation kind inference, trace budget) ────

    private static string InferLlmOperationKind(IReadOnlyList<LlmChatMessage> messages, LlmConversationOptions? options, IReadOnlyList<JsonObject>? tools)
    {
        if (!string.IsNullOrWhiteSpace(options?.ResponseSchema))
        {
            return options.ResponseSchema.Contains("Assessment", StringComparison.OrdinalIgnoreCase) ? "assessment"
                : options.ResponseSchema.Contains("JoinDecision", StringComparison.OrdinalIgnoreCase) ? "join"
                : "structured_response";
        }

        var lastUserMessage = messages.LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        if (lastUserMessage.Contains("Create an executable task graph", StringComparison.OrdinalIgnoreCase) || lastUserMessage.Contains("Revise the execution graph", StringComparison.OrdinalIgnoreCase))
        {
            return "planning";
        }

        if (lastUserMessage.Contains("Execute the next ready tasks", StringComparison.OrdinalIgnoreCase) || lastUserMessage.Contains("Execute the user request by following this", StringComparison.OrdinalIgnoreCase))
        {
            return "execution";
        }

        if (lastUserMessage.Contains("Evaluate the completed task observations", StringComparison.OrdinalIgnoreCase))
        {
            return "join";
        }

        if (lastUserMessage.Contains("Answer the original user request directly now", StringComparison.OrdinalIgnoreCase))
        {
            return "finalization";
        }

        return tools is { Count: > 0 } ? "tool_enabled_conversation" : "conversation";
    }

    private static ExecutionCorrelationContext BuildLlmCorrelationContext(NodeConfig config, WorkflowRunServices services, string operationKind, string spanId)
    {
        var parent = services.CorrelationContext;
        var agentRunId = config.State.TryGetValue(AgentRuntimeKeys.AgentRunIdStateKey, out var rawAgentRunId) ? rawAgentRunId as string : null;

        return new ExecutionCorrelationContext(
            RunId: services.RunId,
            EntityId: parent?.EntityId ?? config.Definition.Id,
            TraceId: parent?.TraceId ?? services.RunId.ToString("N"),
            SpanId: spanId,
            ParentSpanId: agentRunId ?? parent?.SpanId,
            NodeRunId: agentRunId,
            NodeExecutionId: config.Node.Id,
            StepId: operationKind
        );
    }

    private static string ResolveLlmProvider(ResourceBinding binding) => binding.GetString("channel_type") ?? binding.SourceNode.NodeType ?? binding.SourceNodeId;

    private static string ResolveLlmModel(ResourceBinding binding) => binding.GetString("model") ?? binding.GetString("deployment") ?? binding.SourceNodeId;

    private static bool TryGetTraceBudgetState(NodeConfig config, out AgentTraceBudgetState? budgetState)
    {
        budgetState = null;

        if (!config.State.TryGetValue("__ai_agent_trace_budget_state", out var rawBudgetState) || rawBudgetState is not AgentTraceBudgetState typedBudgetState)
        {
            return false;
        }

        budgetState = typedBudgetState;
        return true;
    }

    private static void RefreshTraceBudgetFields(NodeConfig config, WorkflowRunServices services)
    {
        services.UpdateTrace(accumulator =>
        {
            if (accumulator.Root is null)
            {
                return;
            }

            if (!config.State.TryGetValue("__ai_agent_trace_metadata", out var rawTraceMetadata) || rawTraceMetadata is not AgentTraceMetadata traceMetadata)
            {
                return;
            }

            AgentExecutionTraceRootHelper.EnsureExecutionRoot(accumulator, traceMetadata, AgentExecutionTraceRootHelper.ReadIterationCount(accumulator.Root));
        });
    }
}
