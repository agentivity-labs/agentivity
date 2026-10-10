using System.Text.Json.Nodes;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Interactions;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Infrastructure.Stores;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Events;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Execution.Executable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Proxy HIL non-bloquant pour le scope team.
/// <para>
/// <see cref="WaitForHumanReviewAsync"/> prépare la requête et injecte un marqueur opaque
/// dans les messages de session pour permettre la reprise, mais ne dispatche PAS encore le
/// canal SSE. Le dispatch est différé à <see cref="PostRunAsync"/>, appelé par
/// <see cref="AgentHost.RunAgentAsync"/> après que la session est persistée — garantissant que
/// le client ne peut pas répondre avant que la session avec le marqueur soit durable.
/// </para>
/// <para>
/// Sur la prochaine invocation de l'agent, <see cref="TryResumeAsync"/> détecte le marqueur
/// dans la session chargée, récupère la réponse depuis le store, et l'injecte comme résultat
/// de tool avant de relancer la boucle ReAct.
/// </para>
/// </summary>
internal sealed class HilWaitProxy : IAgentHilProxy
{
    // Marqueur opaque injecté comme tool result dans la session pour persister le requestId.
    // Détecté par TryResumeAsync sur la prochaine invocation.
    private const string MarkerPrefix = "[[HIL_PENDING:";
    private const string MarkerSuffix = "]]";

    private readonly PreparedAgentHumanLoopContext _humanLoopContext;
    private readonly ChannelBinding _channel;
    private readonly string _agentId;
    private readonly string? _executionId;
    private readonly IInteractionRequestStore _interactionStore;
    private readonly IRunEventBus? _eventBus;
    private readonly IRepository<RunContext, RunRecord>? _runRepository;
    private readonly string? _teamRunId;
    private readonly IExecutionRecordStore? _executionStore;
    private readonly Guid _runGuid;        // The agent's own RunId (telemetry, clarification ledger)
    private readonly Guid _requestRunGuid; // RunId stamped on InteractionRequests — the TEAM run id when
                                           // in a team, because that's the id endpoints query by
                                           // (respond fallback, cleanup); the agent id otherwise.
    private readonly string _streamId; // The execution's SSE channel identifier
    private readonly RunContext _context;
    private readonly bool _allowsHumanQuestions;
    private readonly ILogger _logger;
    private readonly string? _memberDisplayName;

    // Set by WaitForHumanReviewAsync; consumed and cleared by PostRunAsync.
    private InteractionRequest? _pendingDispatchRequest;

    private HilWaitProxy(PreparedAgentHumanLoopContext humanLoopContext, ChannelBinding channel, RunContext context, string agentId, IInteractionRequestStore interactionStore, IRunEventBus? eventBus, IRepository<RunContext, RunRecord>? runRepository, string? teamRunId, IExecutionRecordStore? executionStore, bool allowsHumanQuestions, ILogger? logger, string? memberDisplayName)
    {
        _humanLoopContext = humanLoopContext;
        _channel = channel;
        _agentId = agentId;
        _executionId = context.Execution.ExecutionId;
        _interactionStore = interactionStore;
        _eventBus = eventBus;
        _runRepository = runRepository;
        _teamRunId = teamRunId;
        _executionStore = executionStore;
        _runGuid = Guid.TryParse(context.RunId, out var rg) ? rg : Guid.NewGuid();
        _requestRunGuid = teamRunId is not null && Guid.TryParse(teamRunId, out var tg) ? tg : _runGuid;
        _streamId = context.Execution.StreamId;
        _context = context;
        _allowsHumanQuestions = allowsHumanQuestions;
        _logger = logger ?? NullLogger.Instance;
        _memberDisplayName = memberDisplayName;
    }

    /// <summary>
    /// Resolves whether the channel supports HIL and what capabilities it exposes.
    /// Used by both standalone and team proxy factories.
    /// </summary>
    public static PreparedAgentHumanLoopContext PrepareHumanLoopContext(ChannelBinding? channel, IInteractionRequestStore? interactionStore, bool allowsHumanQuestions = true)
    {
        var hasChannel = channel is not null && interactionStore is not null;
        var capabilities = hasChannel ? HumanInteractionChannelDispatcher.ResolveAgentHumanLoopCapabilities(channel, allowsHumanQuestions) : AgentHumanLoopCapabilities.None;
        return new PreparedAgentHumanLoopContext(hasChannel, capabilities);
    }

    public static HilWaitProxy? TryCreate(ChannelBinding? channel, RunContext context, PreparedAgentHumanLoopContext humanLoopContext, string agentId, IInteractionRequestStore? interactionStore, IRunEventBus? eventBus, IRepository<RunContext, RunRecord>? runRepository = null, string? teamRunId = null, IExecutionRecordStore? executionStore = null, bool allowsHumanQuestions = true, ILogger? logger = null, string? memberDisplayName = null)
    {
        if (!humanLoopContext.HasInteractionChannel || interactionStore is null || channel is null)
            return null;

        return new HilWaitProxy(humanLoopContext, channel, context, agentId, interactionStore, eventBus, runRepository, teamRunId, executionStore, allowsHumanQuestions, logger, memberDisplayName);
    }

    // ── IAgentHilProxy ────────────────────────────────────────────────────────

    public bool IsEnabled => _humanLoopContext.HasInteractionChannel && _humanLoopContext.Capabilities.SupportsAskHuman;

    public bool AllowsHumanQuestions => _allowsHumanQuestions;

    public IReadOnlyList<JsonObject> ToolDefinitions => IsEnabled ? _humanLoopContext.Capabilities.ToolDefinitions : [];

    /// <summary>
    /// Injects the channel's human-loop instructions (e.g. ChatChannel's guidance on the
    /// <c>interact_with_human</c> tool and its widget vocabulary) into the agent's system
    /// prompt — same source as the workflow path (<c>WorkflowHilProxy.BuildSystemPrompt</c>).
    /// Without this, team/standalone agents see the tool definition but get zero guidance on
    /// when to use it or which widget fits, and default to plain-text-only content.
    /// </summary>
    public string BuildSystemPrompt() =>
        IsEnabled
            ? HumanLoopConversationContextHelper.BuildConnectedHumanLoopSystemPrompt(true, _humanLoopContext.Capabilities, HumanLoopConversationContextHelper.GetClarificationLedger(_runGuid))
            : string.Empty;

    /// <summary>
    /// Détecte un marqueur HIL en attente dans la session chargée.
    /// Retourne non-null pour indiquer à <c>AgentHost</c> d'entrer en mode reprise
    /// (ne pas appender le prompt utilisateur, appeler <see cref="TryResumeAsync"/>).
    /// </summary>
    public PendingHumanReview? GetPendingReview(SessionContext sessionContext)
    {
        var (_, requestId, toolCallId) = FindMarker(sessionContext.RestoredMessages);
        if (requestId == Guid.Empty)
            return null;

        // Retourne un PendingHumanReview minimal — seuls Kind, RequestId et ToolCallId importent ici.
        return new PendingHumanReview(
            Kind: AgentHumanReviewKind.AskHuman,
            RequestId: requestId,
            Iteration: 0,
            Title: string.Empty,
            Question: null,
            QuestionsToAsk: [],
            DraftContent: null,
            AssistantDraft: null,
            ToolCallId: toolCallId,
            ToolName: "ask_human",
            ToolArgumentsJson: "{}",
            InteractionKind: InteractionKind.Input,
            Operation: HumanInteractionOperation.SendAndWait,
            Fields: [],
            ResultMode: AgentHumanLoopResultMode.FreeText,
            ClarificationKeys: [],
            RemainingToolCalls: [],
            CreatedAtUtc: DateTimeOffset.UtcNow
        );
    }

    /// <summary>
    /// Tente de reprendre depuis un HIL suspendu lors d'une invocation précédente.
    /// Retire le marqueur de placeholder de <paramref name="messages"/> et injecte la vraie
    /// réponse humaine comme résultat de tool si elle est disponible dans le store.
    /// </summary>
    public async ValueTask<HumanReviewResumeState> TryResumeAsync(SessionContext sessionContext, List<LlmChatMessage> messages, IReadOnlySet<string> reusableToolNames, CancellationToken ct)
    {
        var (markerContent, requestId, toolCallId) = FindMarker(sessionContext.RestoredMessages);
        if (requestId == Guid.Empty || toolCallId is null)
            return HumanReviewResumeState.None;

        var response = await _interactionStore.GetResponseAsync(requestId, ct).ConfigureAwait(false);
        _logger.LogDebug(
            "[ChatDiag] TryResumeAsync requestId={RequestId} found={Found} ResponseText={ResponseText} Data={Data} Metadata={Metadata}",
            requestId,
            response is not null,
            response?.ResponseText,
            response?.Data is { } data ? string.Join(", ", data.Select(kv => $"{kv.Key}={kv.Value}")) : null,
            response?.Metadata is { Count: > 0 } meta ? string.Join(", ", meta.Select(kv => $"{kv.Key}={kv.Value}")) : null
        );
        if (response is null)
        {
            // The marker survived in the persisted session, but its response is gone from the
            // store (already consumed by an earlier resume, or expired). Left in place, this
            // marker would keep matching on every future turn — GetPendingReview would report it
            // as pending forever, permanently stalling the agent. Clear it here, the same way the
            // success path below does, with a synthetic fallback tool_result so the conversation
            // stays pairing-valid and the agent can proceed instead of being stuck.
            var staleMarkerIndex = messages.FindIndex(m =>
                string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Content, markerContent, StringComparison.Ordinal));

            if (staleMarkerIndex >= 0)
                messages.RemoveRange(staleMarkerIndex, messages.Count - staleMarkerIndex);

            _interactionStore.RemoveRequest(requestId);

            System.Diagnostics.Debug.WriteLine($"[HilWaitProxy.TryResumeAsync] WRN stale HIL marker requestId={requestId} has no stored response — clearing with fallback tool_result.");

            var fallbackExecution = new AgentToolExecution(toolCallId, "ask_human", "{}", "Human response unavailable (request expired or already processed).", false, false);
            return new HumanReviewResumeState([fallbackExecution], [], null, null, null, null, 0);
        }

        // Retire le marqueur ET tous les messages générés après lui dans la même session.
        // L'agent peut avoir produit du texte après ask_human (ex: "J'ai transmis...") avant
        // d'être suspendu — ces messages doivent être supprimés pour que le LLM génère une
        // vraie réponse à la suite de la réponse humaine, plutôt que de voir une conversation
        // déjà terminée et produire un token vide.
        var markerIndex = messages.FindIndex(m =>
            string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Content, markerContent, StringComparison.Ordinal));

        if (markerIndex >= 0)
            messages.RemoveRange(markerIndex, messages.Count - markerIndex);

        _interactionStore.RemoveRequest(requestId);

        // A structured reply (e.g. a multi-field QuestionForm) carries its values in Data, not
        // ResponseText — ResponseText alone is often empty or a generic label for that case.
        // Without folding Data in here, the agent itself never sees the field values it asked
        // for: it only receives "Human responded: ", and everything downstream (its own reply,
        // and whatever of that reply later feeds the team summary) is working from nothing.
        var responseText = response.ResponseText ?? string.Empty;
        var dataText = response.Data is { Count: > 0 } responseData
            ? string.Join("\n", responseData.Select(kv => $"- {kv.Key}: {kv.Value}"))
            : null;
        var toolResultContent = dataText is null
            ? $"Human responded: {responseText}"
            : string.IsNullOrWhiteSpace(responseText)
                ? $"Human responded:\n{dataText}"
                : $"Human responded: {responseText}\n{dataText}";

        _eventBus?.Publish(
            new RunEvent(
                RunId: _runGuid.ToString(),
                DefinitionId: _agentId,
                RunKind: RunKind.Agent,
                Kind: RunEventKind.hil_approved,
                EventName: "agent_hil_responded",
                OccurredAtUtc: new DateTimeOffset(response.RespondedAt, TimeSpan.Zero),
                Payload: new { requestId = response.RequestId },
                ExecutionId: _executionId,
                StreamId: _streamId
            )
        );

        var execution = new AgentToolExecution(toolCallId, "ask_human", "{}", toolResultContent, false, false);
        return new HumanReviewResumeState([execution], [], null, null, null, null, 0);
    }

    /// <summary>
    /// Prépare la requête HIL, l'enregistre dans le store, et injecte un marqueur de
    /// placeholder dans <paramref name="messages"/> — mais ne dispatche PAS encore le canal
    /// SSE. Le dispatch est différé à <see cref="PostRunAsync"/> pour garantir que la session
    /// avec le marqueur est persistée avant que le client puisse répondre.
    /// Retourne le token de suspension (request id) pour que l'appelant propage le statut
    /// <see cref="ExecutableStatus.WaitingFor"/>.
    /// </summary>
    public ValueTask<string?> WaitForHumanReviewAsync(
        AgentTraceMetadata traceMetadata,
        SessionContext sessionContext,
        IReadOnlyList<LlmChatMessage> messages,
        PendingHumanReview pendingReview,
        IReadOnlyList<AgentToolExecution> executedToolCalls,
        bool persistSession,
        CancellationToken cancellationToken
    )
    {
        var primaryQuestion = pendingReview.Question ?? pendingReview.DraftContent ?? pendingReview.Title ?? string.Empty;
        if (string.IsNullOrWhiteSpace(primaryQuestion))
            return ValueTask.FromResult<string?>(null);

        // If a thread already exists in this context, tell ChatChannel to reuse it
        // rather than creating a second thread with a different ID.
        var request = HumanLoopHelper.BuildInteractionRequest(
            pendingReview,
            _requestRunGuid,
            _agentId,
            _channel,
            executionId: _executionId,
            streamId: _context.Execution.StreamId,
            existingThreadId: _context.InteractionThreadId,
            memberEntityId: _agentId,
            memberDisplayName: _memberDisplayName
        );

        // Store the pending dispatch — PostRunAsync will fire it after the session is saved.
        _pendingDispatchRequest = request;

        // Ensure a valid LLM conversation structure before adding the tool-result marker.
        // The LLM protocol requires every tool-result message to be preceded by an assistant
        // message that declares the matching tool_call id. When the HIL comes from an explicit
        // tool call (e.g. ask_human), the caller already added that assistant message.
        // When the HIL comes from the qualification path (TryWaitForHumanInputAsync), no such
        // message exists yet — so we add it here as the single authoritative place that owns this invariant.
        if (messages is List<LlmChatMessage> msgList)
        {
            var hasMatchingCall = msgList.Any(m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                m.ToolCalls?.Any(tc => string.Equals(tc.Id, pendingReview.ToolCallId, StringComparison.Ordinal)) == true);

            if (!hasMatchingCall)
                msgList.Add(new LlmChatMessage("assistant", string.Empty,
                    ToolCalls: [new Agentivity.Infrastructure.Core.Llm.LlmToolCall(pendingReview.ToolCallId, pendingReview.ToolName, pendingReview.ToolArgumentsJson)]));
        }

        var marker = BuildMarker(request.Id);
        (messages as List<LlmChatMessage>)?.Add(new LlmChatMessage("tool", marker, ToolCallId: pendingReview.ToolCallId, IsPlaceholder: true));

        return ValueTask.FromResult<string?>(request.Id.ToString());
    }

    /// <summary>
    /// Dispatches the pending review's content immediately (e.g. <c>interact_with_human</c>
    /// called with <c>mode: "send"</c>) — no marker, no suspension, the agent loop continues
    /// right after this call returns.
    /// </summary>
    public async ValueTask<string> SendWithoutWaitingAsync(PendingHumanReview pendingReview, CancellationToken cancellationToken)
    {
        var request = HumanLoopHelper.BuildInteractionRequest(
            pendingReview,
            _requestRunGuid,
            _agentId,
            _channel,
            operationOverride: HumanInteractionOperation.Send,
            executionId: _executionId,
            streamId: _context.Execution.StreamId,
            existingThreadId: _context.InteractionThreadId,
            memberEntityId: _agentId,
            memberDisplayName: _memberDisplayName
        );

        await HumanInteractionChannelDispatcher.ExecuteAsync(_channel, request, cancellationToken).ConfigureAwait(false);
        return "Message delivered to the user. Do not repeat or rephrase it. If nothing else remains to do, end your turn with a short final plain-text reply (no tool call).";
    }

    /// <summary>
    /// Appelé par <see cref="AgentHost.RunAgentAsync"/> après que la session est persistée.
    /// Si une requête HIL est en attente, sauvegarde le run en état Paused puis dispatche
    /// le canal SSE — dans cet ordre, pour que le handler de reprise soit prêt avant que
    /// le client ne réponde.
    /// </summary>
    public async ValueTask PostRunAsync(CancellationToken ct)
    {
        var request = _pendingDispatchRequest;
        System.Diagnostics.Debug.WriteLine($"[HilWaitProxy.PostRunAsync] request={request?.Id} streamId={_streamId} teamRunId={_teamRunId}");
        if (request is null)
            return;

        _pendingDispatchRequest = null;

        // 1. Mark the run as WaitingForInput via the generic IRunRepository so the client
        //    knows the run is paused before they can respond. No type-specific store needed.
        if (_runRepository is not null && _teamRunId is not null)
        {
            var ctx = await _runRepository.FindAsync(_teamRunId, ct).ConfigureAwait(false);
            if (ctx is not null)
            {
                ctx.SetStatus(RunStatus.WaitingForInput);
                await _runRepository.AddAsync(ctx, ct).ConfigureAwait(false);
            }
        }

        // 2. Dispatch SSE — client now sees the question and can reply.
        System.Diagnostics.Debug.WriteLine($"[HilWaitProxy.PostRunAsync] Dispatching HIL to channel runId={_runGuid} streamId={_streamId}");
        var dispatch = await HumanInteractionChannelDispatcher.ExecuteAsync(_channel, request, ct).ConfigureAwait(false);
        System.Diagnostics.Debug.WriteLine($"[HilWaitProxy.PostRunAsync] Dispatch done, thread_id={dispatch.Request.Metadata?.GetValueOrDefault("chat.thread_id")}");
        await _interactionStore.SaveRequestAsync(dispatch.Request, ct).ConfigureAwait(false);

        // 3. Propagate the thread ID so AgentExecutable knows a thread already exists
        //    and won't publish a second CHAT_THREAD_CREATED.
        if (dispatch.Request.Metadata.TryGetValue("chat.thread_id", out var threadIdObj) && threadIdObj?.ToString() is { Length: > 0 } threadId)
        {
            _context.InteractionThreadId ??= threadId;

            // 4. Persist the threadId in ExecutionRecord.
            if (_executionStore is not null && !string.IsNullOrWhiteSpace(_executionId))
            {
                var execRecord = await _executionStore.GetAsync(_executionId, ct).ConfigureAwait(false);
                if (execRecord is not null && execRecord.InteractionThreadId != threadId)
                    await _executionStore.SaveAsync(execRecord with { InteractionThreadId = threadId }, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildMarker(Guid requestId) => $"{MarkerPrefix}{requestId:D}{MarkerSuffix}";

    /// <summary>
    /// Cherche le marqueur HIL dans <paramref name="restoredMessages"/>.
    /// Retourne (markerContent, requestId, toolCallId) ou (null, Empty, null) si absent.
    /// </summary>
    private static (string? MarkerContent, Guid RequestId, string? ToolCallId) FindMarker(IReadOnlyList<LlmChatMessage> restoredMessages)
    {
        // Search in reverse so we find the MOST RECENT marker — earlier markers have
        // already been processed and their responses deleted from the store.
        for (var i = restoredMessages.Count - 1; i >= 0; i--)
        {
            var msg = restoredMessages[i];
            if (!string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = msg.Content ?? string.Empty;
            if (!content.StartsWith(MarkerPrefix, StringComparison.Ordinal) || !content.EndsWith(MarkerSuffix, StringComparison.Ordinal))
                continue;

            var idStr = content[MarkerPrefix.Length..^MarkerSuffix.Length];
            if (!Guid.TryParse(idStr, out var requestId))
                continue;

            return (content, requestId, msg.ToolCallId);
        }

        return (null, Guid.Empty, null);
    }
}
