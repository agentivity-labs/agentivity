using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Interactions;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts.Events.AgentControl;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Workflow implementation of <see cref="IAgentHilProxy"/>.
/// Owns HIL channel resolution, resume/suspend logic and <see cref="IInteractionRequestStore"/> access.
/// </summary>
public sealed class WorkflowHilProxy : IAgentHilProxy
{
    private readonly PreparedAgentHumanLoopContext _humanLoopContext;
    private readonly bool _isEnabled;
    private readonly NodeConfig _config;
    private readonly WorkflowRunServices _services;
    private readonly IInteractionRequestStore _interactionStore;
    private readonly IReadOnlyList<JsonObject> _toolDefinitions;

    public WorkflowHilProxy(PreparedAgentHumanLoopContext humanLoopContext, SessionContext sessionContext, NodeConfig config, WorkflowRunServices services, IInteractionRequestStore interactionStore)
    {
        _humanLoopContext = humanLoopContext;
        _isEnabled = humanLoopContext.IsHumanLoopEnabledFor(sessionContext);
        _config = config;
        _services = services;
        _interactionStore = interactionStore;

        _toolDefinitions = _isEnabled ? humanLoopContext.Capabilities.ToolDefinitions.Select(t => (JsonObject)t.DeepClone()).ToList() : [];
    }

    public bool IsEnabled => _isEnabled;

    public IReadOnlyList<JsonObject> ToolDefinitions => _toolDefinitions;

    public string BuildSystemPrompt()
    {
        var clarifications = HumanLoopConversationContextHelper.GetClarificationLedger(_services.RunId);
        return HumanLoopConversationContextHelper.BuildConnectedHumanLoopSystemPrompt(_isEnabled, _humanLoopContext.Capabilities, clarifications);
    }

    public PendingHumanReview? GetPendingReview(SessionContext sessionContext) => HumanLoopHelper.GetPendingHumanReview(sessionContext.State.Metadata);

    public ValueTask<HumanReviewResumeState> TryResumeAsync(SessionContext sessionContext, List<LlmChatMessage> messages, IReadOnlySet<string> reusableToolNames, CancellationToken ct) => TryResumeHumanReviewAsync(sessionContext, messages, reusableToolNames, ct);

    // ── HIL channel resolution ────────────────────────────────────────────────

    public static ChannelBinding? TryResolveInteractionChannel(NodeConfig config, WorkflowRunServices services)
    {
        var binding = ResourceResolver.TryResolve(config, "channel", ResourcePortCategory.InteractionChannel);
        if (binding is null)
        {
            return null;
        }

        if (NodeInstanceRegistry.GetAs<IHumanInteractionChannel>(binding.SourceNode.NodeType) is not { } channel)
            throw new InvalidOperationException($"Node '{binding.SourceNode.NodeType}' does not implement IHumanInteractionChannel.");
        return new ChannelBinding(ChannelType: channel.ChannelType, Credentials: binding.GetString("credentials"), Respondent: binding.GetString("respondent"), Properties: binding);
    }

    public static PreparedAgentHumanLoopContext PrepareHumanLoopContext(NodeConfig config, WorkflowRunServices services)
    {
        ArgumentNullException.ThrowIfNull(config);
        var reviewChannel = TryResolveInteractionChannel(config, services);
        return new PreparedAgentHumanLoopContext(reviewChannel is not null, HumanInteractionChannelDispatcher.ResolveAgentHumanLoopCapabilities(reviewChannel));
    }

    // ── HIL resume ───────────────────────────────────────────────────────────

    private async ValueTask<HumanReviewResumeState> TryResumeHumanReviewAsync(SessionContext sessionContext, List<LlmChatMessage> messages, IReadOnlySet<string> reusableToolNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);
        ArgumentNullException.ThrowIfNull(messages);
        _ = reusableToolNames;

        var responseKey = $"__interaction_response_{_config.Node.Id}";
        if (!_config.State.TryGetValue(responseKey, out var responseObject) || responseObject is not InteractionResponse response)
        {
            return HumanReviewResumeState.None;
        }

        _config.State.Remove(responseKey);

        var pendingReview = HumanLoopHelper.GetPendingHumanReview(sessionContext.State.Metadata);
        if (pendingReview is null)
        {
            return HumanReviewResumeState.None;
        }

        if (response.RequestId != pendingReview.RequestId)
        {
            throw new InvalidOperationException("The human review response does not match the pending AI agent review request.");
        }

        _interactionStore.RemoveRequest(response.RequestId);

        sessionContext.State.Metadata.Remove(HumanLoopHelper.PendingHumanReviewMetadataKey);
        sessionContext.State.Metadata["last_human_review_state"] = response.Outcome.ToString();
        sessionContext.State.Metadata["last_human_review_kind"] = pendingReview.Kind.ToString();
        sessionContext.State.Metadata["last_human_review_feedback"] = HumanLoopHelper.GetInteractionResponseValue(response, "feedback") ?? string.Empty;
        sessionContext.State.Metadata["last_human_review_responded_at_utc"] = response.RespondedAt.ToString("O");

        if (pendingReview.Kind != AgentHumanReviewKind.AskHuman)
        {
            throw new InvalidOperationException($"Unsupported pending human-loop request kind '{pendingReview.Kind}'.");
        }

        var answer = HumanLoopHelper.ResolveHumanReviewAnswer(response);
        HumanLoopConversationContextHelper.UpdateClarificationLedger(_services.RunId, pendingReview, response, answer);
        HumanLoopConversationContextHelper.PersistClarificationLedger(_services.RunId, sessionContext.State.Metadata);
        await EmitHumanInterventionCompletedAsync(pendingReview, response, cancellationToken).ConfigureAwait(false);
        HumanLoopConversationContextHelper.RemovePendingHumanReviewArtifacts(messages, pendingReview);

        return HumanLoopHelper.ResumeAskHumanReview(pendingReview, response);
    }

    // ── HIL suspend ──────────────────────────────────────────────────────────

    public async ValueTask<string?> WaitForHumanReviewAsync(
        AgentTraceMetadata traceMetadata,
        SessionContext sessionContext,
        IReadOnlyList<LlmChatMessage> messages,
        PendingHumanReview pendingReview,
        IReadOnlyList<AgentToolExecution> executedToolCalls,
        bool persistSession,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(traceMetadata);
        ArgumentNullException.ThrowIfNull(sessionContext);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(pendingReview);
        ArgumentNullException.ThrowIfNull(executedToolCalls);

        var channel = TryResolveInteractionChannel(_config, _services) ?? throw new InvalidOperationException("The AI agent human-loop channel is not available for this execution.");

        HumanLoopConversationContextHelper.PersistClarificationLedger(_services.RunId, sessionContext.State.Metadata);
        sessionContext.State.Metadata[HumanLoopHelper.PendingHumanReviewMetadataKey] = System.Text.Json.JsonSerializer.SerializeToNode(pendingReview);
        sessionContext.State.Metadata["last_human_review_state"] = "pending";
        sessionContext.State.Metadata["last_human_review_kind"] = pendingReview.Kind.ToString();
        sessionContext.State.Metadata["last_human_review_requested_at_utc"] = pendingReview.CreatedAtUtc.ToString("O");
        WorkflowSessionProxy.CaptureRemoteMcpSessionState(_config, _services, sessionContext);

        var persistedMessages = await WorkflowSessionProxy.SaveSessionStateAsync(_config, _services, sessionContext, messages, persistSession, cancellationToken).ConfigureAwait(false);

        var request = BuildHumanReviewRequest(_config, _services, channel, pendingReview);
        request = await InteractionChannelDispatcher.DispatchAsync(channel, request, cancellationToken).ConfigureAwait(false);
        await _interactionStore.SaveRequestAsync(request, cancellationToken).ConfigureAwait(false);
        await EmitHumanInterventionRequestedAsync(pendingReview, channel, cancellationToken).ConfigureAwait(false);

        _services.PublishStatus($"Suspending AI agent for human review: {pendingReview.Kind}.");
        throw new WorkflowSuspendedException(request, BuildSuspendedOutputs(sessionContext, persistedMessages.Count, executedToolCalls, pendingReview));
    }

    /// <summary>
    /// Dispatches the pending review's content immediately (e.g. <c>interact_with_human</c>
    /// called with <c>mode: "send"</c>) — no session mutation, no suspension.
    /// </summary>
    public async ValueTask<string> SendWithoutWaitingAsync(PendingHumanReview pendingReview, CancellationToken cancellationToken)
    {
        var channel = TryResolveInteractionChannel(_config, _services) ?? throw new InvalidOperationException("The AI agent human-loop channel is not available for this execution.");
        var request = BuildHumanReviewRequest(_config, _services, channel, pendingReview) with { Operation = HumanInteractionOperation.Send };
        await InteractionChannelDispatcher.DispatchAsync(channel, request, cancellationToken).ConfigureAwait(false);
        return "Message delivered to the user. Do not repeat or rephrase it. If nothing else remains to do, end your turn with a short final plain-text reply (no tool call).";
    }

    private ValueTask EmitHumanInterventionRequestedAsync(PendingHumanReview pendingReview, ChannelBinding channel, CancellationToken cancellationToken) =>
        _services.TelemetryEmitter?.EmitAsync(
            WorkflowTraceProxy.BuildAgentControlCorrelationContext(_config, _services, pendingReview.RequestId.ToString("N"), $"human-review-{pendingReview.RequestId:N}"),
            new HumanInterventionRequestedEvent(
                pendingReview.RequestId.ToString("N"),
                pendingReview.Question ?? pendingReview.QuestionsToAsk.FirstOrDefault() ?? pendingReview.Title,
                pendingReview.CreatedAtUtc.UtcDateTime,
                pendingReview.DraftContent ?? pendingReview.AssistantDraft,
                channel.ChannelType
            ),
            cancellationToken
        )
        ?? ValueTask.CompletedTask;

    private ValueTask EmitHumanInterventionCompletedAsync(PendingHumanReview pendingReview, InteractionResponse response, CancellationToken cancellationToken)
    {
        var requestedAtUtc = pendingReview.CreatedAtUtc.UtcDateTime;
        var completedAtUtc = response.RespondedAt;
        return _services.TelemetryEmitter?.EmitAsync(
                WorkflowTraceProxy.BuildAgentControlCorrelationContext(_config, _services, pendingReview.RequestId.ToString("N"), $"human-review-{pendingReview.RequestId:N}"),
                new HumanInterventionCompletedEvent(
                    pendingReview.RequestId.ToString("N"),
                    requestedAtUtc,
                    completedAtUtc,
                    (long)Math.Max(0, (completedAtUtc - requestedAtUtc).TotalMilliseconds),
                    response.Outcome.ToString(),
                    response.ResponseText ?? HumanLoopHelper.GetInteractionResponseValue(response, "feedback")
                ),
                cancellationToken
            )
            ?? ValueTask.CompletedTask;
    }

    private static InteractionRequest BuildHumanReviewRequest(NodeConfig config, WorkflowRunServices services, ChannelBinding channel, PendingHumanReview pendingReview)
    {
        var timeoutMinutes = channel.Properties.GetInt("timeout_minutes") ?? 60;
        return HumanLoopHelper.BuildInteractionRequest(
            pendingReview,
            services.RunId,
            config.Node.Id,
            channel,
            executionId: services.ExecutionId,
            streamId: services.StreamId,
            timeout: timeoutMinutes > 0 ? TimeSpan.FromMinutes(timeoutMinutes) : null
        );
    }

    private static IReadOnlyDictionary<string, object?> BuildSuspendedOutputs(SessionContext sessionContext, int sessionMessageCount, IReadOnlyList<AgentToolExecution> executedToolCalls, PendingHumanReview? pendingReview = null)
    {
        var visibleToolCalls = executedToolCalls.ToList();
        if (pendingReview is { ToolName: not null, ToolCallId: not null, ToolArgumentsJson: not null })
        {
            visibleToolCalls.Add(new AgentToolExecution(pendingReview.ToolCallId, pendingReview.ToolName, pendingReview.ToolArgumentsJson, string.Empty, false, false));
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["finish_reason"] = "waiting_for_input",
            ["tool_calls"] = System.Text.Json.JsonSerializer.Serialize(visibleToolCalls),
            ["session_id"] = sessionContext.Enabled ? sessionContext.SessionId : null,
            ["session_scope"] = sessionContext.Enabled ? sessionContext.Scope : null,
            ["session_restored"] = sessionContext.Enabled && sessionContext.Restored,
            ["session_message_count"] = sessionMessageCount,
        };
    }
}
