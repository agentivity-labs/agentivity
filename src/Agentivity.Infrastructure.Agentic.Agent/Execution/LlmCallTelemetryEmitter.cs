using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events.Llm;

namespace Agentivity.Infrastructure.Agentic.Agent.Execution;

/// <summary>
/// Shared helper for emitting <see cref="LlmCallStartedEvent"/>/<see cref="LlmCallCompletedEvent"/>
/// telemetry around an LLM conversation call.
/// <para>
/// Used by both <c>WorkflowAgentExecutionRuntimePort</c> (via <c>WorkflowAgentLlmCoordinator</c>) and
/// <c>StandaloneAgentExecutionRuntimePort</c> so the two <see cref="IAgentLlmProxy"/>
/// implementations emit identical telemetry. This is monitoring-only telemetry and is
/// intentionally independent of the chat message stream (see <c>AgentChatMessagePublisher</c>).
/// </para>
/// </summary>
public static class LlmCallTelemetryEmitter
{
    public static ValueTask EmitStartedAsync(IAgentTelemetryEmitter? telemetryEmitter, ExecutionCorrelationContext? correlation, string provider, string model, string operationKind, DateTime startedAtUtc, string? toolChoiceMode, CancellationToken cancellationToken)
    {
        if (telemetryEmitter is null || correlation is null)
            return ValueTask.CompletedTask;

        return telemetryEmitter.EmitAsync(correlation, new LlmCallStartedEvent(provider, model, operationKind, startedAtUtc, toolChoiceMode), cancellationToken);
    }

    /// <summary>Emits a successful <see cref="LlmCallCompletedEvent"/> (monitoring telemetry only).</summary>
    public static ValueTask EmitCompletedAsync(
        IAgentTelemetryEmitter? telemetryEmitter,
        ExecutionCorrelationContext? correlation,
        string provider,
        string model,
        string operationKind,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        LlmConversationResponse response,
        CancellationToken cancellationToken
    )
    {
        if (telemetryEmitter is null || correlation is null)
            return ValueTask.CompletedTask;

        return telemetryEmitter.EmitAsync(
            correlation,
            new LlmCallCompletedEvent(provider, model, operationKind, startedAtUtc, completedAtUtc, (long)Math.Max(0, (completedAtUtc - startedAtUtc).TotalMilliseconds), TotalTokens: response.TokensUsed, FinishReason: response.FinishReason, Succeeded: true),
            cancellationToken
        );
    }

    /// <summary>Emits a failed/errored <see cref="LlmCallCompletedEvent"/> (no content).</summary>
    public static ValueTask EmitFailedAsync(IAgentTelemetryEmitter? telemetryEmitter, ExecutionCorrelationContext? correlation, string provider, string model, string operationKind, DateTime startedAtUtc, DateTime completedAtUtc, string errorMessage, CancellationToken cancellationToken)
    {
        if (telemetryEmitter is null || correlation is null)
            return ValueTask.CompletedTask;

        return telemetryEmitter.EmitAsync(correlation, new LlmCallCompletedEvent(provider, model, operationKind, startedAtUtc, completedAtUtc, (long)Math.Max(0, (completedAtUtc - startedAtUtc).TotalMilliseconds), Succeeded: false, ErrorMessage: errorMessage), cancellationToken);
    }
}
