using System.Text.Json.Nodes;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Core.Llm.Moderation;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Agentic.Agent;

public sealed partial class AgentHost
{
    public IAgentEngine Engine { get; }

    /// <summary>Builds the turn-conclusion tool for agents whose context does not supply its own.</summary>
    public ITurnConclusionToolFactory TurnConclusionFactory { get; }

    private readonly IAgentPolicyRuntime _policyRuntime;
    private readonly AgentGuardrailRuntime _guardrailRuntime;

    /// <summary>
    /// <paramref name="moderationService"/> is optional so every existing test call site
    /// (~40 of them, all bare <c>new AgentHost(engine)</c>, zero DI) keeps compiling — a Moderation
    /// guardrail simply fails open (see <see cref="AgentGuardrailRuntime"/>) when it's null. The
    /// one production call site (<c>ServiceCollectionExtensions.AddAgentHost</c>) resolves the real
    /// implementation from the container.
    /// </summary>
    public AgentHost(IAgentEngine engine, IModerationService? moderationService = null, ITurnConclusionToolFactory? turnConclusionFactory = null)
    {
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        TurnConclusionFactory = turnConclusionFactory ?? GenericTurnConclusionToolFactory.Instance;
        _policyRuntime = new AgentPolicyRuntime();
        _guardrailRuntime = new AgentGuardrailRuntime(moderationService);
    }

    public string Id => Engine.Id;

    public IReadOnlyCollection<IAgenticModeRuntime> ModeRuntimes => Engine.ModeRuntimes;

    public IReadOnlyCollection<string> SupportedModes => Engine.SupportedModes;

    public bool CanHandle(string agentMode) => Engine.CanHandle(agentMode);

    public bool TryGetModeRuntime(string agentMode, out IAgenticModeRuntime? modeRuntime) => Engine.TryGetModeRuntime(agentMode, out modeRuntime);

    public ResolvedAgenticEngineRuntime ResolveRuntime(string agentMode) => AgenticModeRuntimeResolver.Resolve(Engine, agentMode);

    public IAgentPolicyRuntime PolicyRuntime => _policyRuntime;

    /// <param name="invokeLlm">The agent's own connected LLM, for LlmJudge guardrails — pass null
    /// when unavailable at this call site (those guardrails then fail open).</param>
    /// <param name="matchSink">When supplied, every non-blocking (Warn) match found gets appended
    /// here — the caller's own accumulator across the whole run (see
    /// <see cref="AgentRunServices.GuardrailWarningSink"/>). A Block match never reaches this: the
    /// method throws before returning, same as before this parameter existed.</param>
    public async ValueTask<string?> ApplyGuardrailsAsync(
        IReadOnlyList<AgentGuardrailDefinition> guardrails,
        AgentGuardrailStage stage,
        string? content,
        bool allowNullOrEmptyContent,
        Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<JsonObject>?, LlmConversationOptions, CancellationToken, ValueTask<LlmConversationResponse>>? invokeLlm,
        CancellationToken cancellationToken,
        List<AgentGuardrailMatch>? matchSink = null
    )
    {
        ArgumentNullException.ThrowIfNull(guardrails);

        if (string.IsNullOrWhiteSpace(content))
        {
            return allowNullOrEmptyContent ? content : string.Empty;
        }

        var application = await _guardrailRuntime.ApplyAsync(guardrails, stage, content, invokeLlm, cancellationToken).ConfigureAwait(false);
        if (application.IsBlocked)
        {
            throw new InvalidOperationException(application.Matches.Last().Message);
        }

        if (application.Matches.Count > 0)
        {
            matchSink?.AddRange(application.Matches);
        }

        return application.Content;
    }

    public static AgentHost LoadFromConfiguration(Microsoft.Extensions.Configuration.IConfiguration configuration, string? baseDirectory = null) => new(AgenticEngineLoader.LoadFromConfiguration(configuration, baseDirectory));

    /// <summary>
    /// Unified agent execution pipeline.
    /// <para>
    /// Resolves tools, builds prompt layers and trace metadata, handles HIL resume,
    /// constructs <see cref="AgentConfig"/> + <see cref="AgentRunState"/>, executes the
    /// mode runtime, and finalises the response.
    /// </para>
    /// <para>
    /// All context-specific concerns (tool listing, HIL suspend/resume, trace writes)
    /// are injected via proxy interfaces so this method stays context-agnostic —
    /// callable from <c>AiNode</c> (workflow), <c>TeamAgent</c> (team), and
    /// <c>Agent</c> (standalone) with matching proxy implementations.
    /// </para>
    /// </summary>
    /// <param name="systemPrompt">Base system instructions.</param>
    /// <param name="prompt">Resolved user prompt for this run.</param>
    /// <param name="responseSchema">JSON Schema for structured response finalization (empty = disabled).</param>
    /// <param name="memoryContext">Retrieved memory/RAG context (empty = none).</param>
    /// <param name="modeRuntime">The resolved agentic mode runtime to execute.</param>
    /// <param name="runtimeArguments">Mode-specific runtime parameter values (empty dict for standalone).</param>
    /// <param name="runtimeParameters">Resolved LLM knobs (temperature, maxTokens, toolChoice, etc.).</param>
    /// <param name="resolvedPolicies">Resolved agent policies for this run.</param>
    /// <param name="resolvedGuardrails">Resolved guardrail definitions for this run.</param>
    /// <param name="persistSession">Whether the session should be persisted at end of run.</param>
    /// <param name="sessionContext">Session context resolved before the run (use <see cref="SessionContext.Disabled"/> when no session).</param>
    /// <param name="toolsProxy">Provides connected tool definitions and custom widget tools.</param>
    /// <param name="hilProxy">Provides HIL enablement, tool definitions, resume state, and the runtime port.</param>
    /// <param name="traceProxy">Receives trace events (no-op for contexts without tracing).</param>
    /// <param name="runtimePort">LLM invocation + tool execution port.</param>
    /// <param name="contextServices">Pre-populated run services (RunId, CorrelationContext, Checkpoint, PublishStatus, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask<AgentRunResult> RunAsync(
        string systemPrompt,
        string prompt,
        string responseSchema,
        string memoryContext,
        IAgenticModeRuntime modeRuntime,
        IReadOnlyDictionary<string, ArgumentDefinition> runtimeArguments,
        AgentRuntimeParameters runtimeParameters,
        ResolvedAgentPolicies resolvedPolicies,
        IReadOnlyList<AgentGuardrailDefinition> resolvedGuardrails,
        bool persistSession,
        SessionContext sessionContext,
        IAgentToolsProxy toolsProxy,
        IAgentHilProxy hilProxy,
        IAgentTraceProxy traceProxy,
        IAgentLlmProxy runtimePort,
        AgentRunServices contextServices,
        RunContext context,
        CancellationToken ct,
        IReadOnlyList<ImageAttachment>? images = null
    )
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(modeRuntime);
        ArgumentNullException.ThrowIfNull(runtimeArguments);
        ArgumentNullException.ThrowIfNull(resolvedPolicies);
        ArgumentNullException.ThrowIfNull(resolvedGuardrails);
        ArgumentNullException.ThrowIfNull(sessionContext);
        ArgumentNullException.ThrowIfNull(toolsProxy);
        ArgumentNullException.ThrowIfNull(hilProxy);
        ArgumentNullException.ThrowIfNull(traceProxy);
        ArgumentNullException.ThrowIfNull(runtimePort);
        ArgumentNullException.ThrowIfNull(contextServices);
        ArgumentNullException.ThrowIfNull(context);

        // Shared across every guardrail evaluation for this run (see
        // AgentRunServices.GuardrailWarningSink) — one list, mutated in place, read back at the
        // very end into AgentRunResult.GuardrailWarnings. No merging needed anywhere.
        var guardrailWarnings = new List<AgentGuardrailMatch>();

        // ── 1. Tool catalog ───────────────────────────────────────────────────
        var connectedToolDefs = modeRuntime.Capabilities.UsesConnectedTools ? await toolsProxy.ListConnectedToolsAsync(ct).ConfigureAwait(false) : (IReadOnlyList<JsonObject>)[];

        var turnConclusionTool = toolsProxy.GetTurnConclusionTool() ?? TurnConclusionFactory.CreateDefault(toolsProxy.AllowsNeedsInput);

        // AG-UI custom widgets (app-declared on the chat channel) aren't part of this list — they
        // reach the LLM as interact_with_human content-block options, already folded into
        // hilProxy.ToolDefinitions by ChatChannel.ResolveInteractionWidgets.
        var hilToolDefs = hilProxy.IsEnabled ? hilProxy.ToolDefinitions : (IReadOnlyList<JsonObject>)[];
        var hilToolDefsByName = hilToolDefs.ToDictionary(LlmToolDefinitionJson.GetRequiredName, t => t, StringComparer.OrdinalIgnoreCase);

        var allToolDefs = HumanLoopHelper.AddNativeHumanLoopTools(connectedToolDefs, hilToolDefs);

        var reusableToolNames = allToolDefs.Where(LlmToolDefinitionJson.SupportsToolStateReuse).Select(LlmToolDefinitionJson.GetRequiredName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (runtimeParameters.ToolChoiceMode == LlmToolChoiceMode.Required && allToolDefs.Count == 0)
            throw new InvalidOperationException("Tool choice mode is 'required' but no AI agent tools are connected.");

        contextServices.PublishStatus?.Invoke($"Executing AI agent with {allToolDefs.Count} tool(s)...");

        // ── 2. Prompt layers ──────────────────────────────────────────────────
        var executionPromptRules = _policyRuntime.GetPolicyRules(AgentPolicyContext.Execution, resolvedPolicies);
        var pendingHumanReview = hilProxy.GetPendingReview(sessionContext);
        var clarificationLedger = HumanLoopConversationContextHelper.GetClarificationLedger(contextServices.RunId);

        ValueTask<string?> ApplyPromptGuardrailAsync(AgentGuardrailStage stage, string content) => ApplyGuardrailsAsync(resolvedGuardrails, stage, content, allowNullOrEmptyContent: true, runtimePort.InvokeLlmConversationAsync, ct, guardrailWarnings);

        var executionPolicySystemPrompt = await AgentPromptLayers.BuildExecutionPolicyPromptAsync(executionPromptRules, ApplyPromptGuardrailAsync).ConfigureAwait(false);
        var agentWorkspacePrompt = await AgentPromptLayers.BuildAgentWorkspacePromptAsync(sessionContext.State.Metadata, ApplyPromptGuardrailAsync).ConfigureAwait(false);
        var humanLoopSystemPrompt = hilProxy.BuildSystemPrompt();

        // ── 3. Initial messages ───────────────────────────────────────────────
        //
        // RestoredMessages = this agent's own prior conversation (this position's session, if it
        // has one) — its continuity across HIL resumes AND across being revisited later in a
        // handoff chain. Always used, never suppressed: a team member revisited later in the same
        // run should remember its own earlier exchange, exactly like a standalone/workflow agent
        // would. context.LastTranscriptSummary is the single rolling-summary channel — this run's
        // own private TranscriptScope for a standalone/workflow agent, or the team's shared scope
        // for a team member (so a member automatically sees what every other member has done, and
        // its own turns compact into the same scope everyone else reads from).
        var messages = AgentConversationBuilder.BuildInitialMessages(systemPrompt, prompt, memoryContext, executionPolicySystemPrompt, agentWorkspacePrompt, humanLoopSystemPrompt, (context.LastTranscriptSummary ?? string.Empty), sessionContext.RestoredMessages, appendPrompt: pendingHumanReview is null, teamContextPrompt: context.TeamContextPrompt, images: images);

        var requiresStructuredResponseFinalization = allToolDefs.Count > 0 && !string.IsNullOrWhiteSpace(responseSchema);

        // ── 4. Trace metadata ─────────────────────────────────────────────────
        var traceMetadata = new AgentTraceMetadata(
            Prompt: prompt,
            ToolNames: allToolDefs.Select(LlmToolDefinitionJson.GetRequiredName).ToList(),
            AgentMode: modeRuntime.ModeId,
            ToolChoiceMode: runtimeParameters.ToolChoiceMode.ToString(),
            AllowParallelToolCalls: runtimeParameters.AllowParallelToolCalls,
            StructuredResponseEnabled: requiresStructuredResponseFinalization,
            HumanLoopEnabled: hilProxy.IsEnabled,
            SessionEnabled: sessionContext.Enabled,
            Temperature: runtimeParameters.Temperature,
            MaxTokens: runtimeParameters.MaxTokens,
            MaxIterations: runtimeParameters.MaxIterations,
            ResponseQualityThreshold: runtimeParameters.ResponseQualityThreshold > 0 ? runtimeParameters.ResponseQualityThreshold : null,
            PlanQualityThreshold: null,
            BudgetState: new AgentTraceBudgetState(),
            ResolvedPolicies: resolvedPolicies,
            ResolvedGuardrails: resolvedGuardrails
        );

        contextServices.SetState?.Invoke("__ai_agent_trace_budget_state", traceMetadata.BudgetState);
        contextServices.SetState?.Invoke("__ai_agent_trace_metadata", traceMetadata);

        traceProxy.Initialize(traceMetadata);
        traceProxy.AppendEvent(traceMetadata, new AgentTraceEvent(0, "policies_resolved", null, [], Policies: resolvedPolicies));
        traceProxy.AppendEvent(traceMetadata, new AgentTraceEvent(0, "guardrails_resolved", null, [], Guardrails: resolvedGuardrails));

        var clarificationSnapshot = HumanLoopConversationContextHelper.BuildClarificationLedgerTraceSnapshot(clarificationLedger);
        traceProxy.UpsertContextLayersSnapshot(
            traceMetadata,
            0,
            BuildContextLayersSnapshot(
                systemPrompt,
                memoryContext,
                executionPolicySystemPrompt,
                agentWorkspacePrompt,
                humanLoopSystemPrompt,
                clarificationSnapshot,
                (context.LastTranscriptSummary ?? string.Empty),
                sessionContext.RestoredMessages.Count,
                appendPrompt: pendingHumanReview is null,
                includeContinuationContext: false
            )
        );

        // ── 5. HIL resume ─────────────────────────────────────────────────────
        var executedToolCalls = new List<AgentToolExecution>();
        var resumeState = await hilProxy.TryResumeAsync(sessionContext, messages, reusableToolNames, ct).ConfigureAwait(false);

        string? finalResponse = null;
        var finishReason = resumeState.FinishReason ?? string.Empty;
        var executedIterations = resumeState.IterationsCompleted;
        executedToolCalls.AddRange(resumeState.ExecutedToolCalls);

        HumanLoopConversationContextHelper.ApplyHumanReviewResumeState(messages, resumeState, ref finalResponse, traceEvent => traceProxy.AppendEvent(traceMetadata, traceEvent));

        traceProxy.UpsertContextLayersSnapshot(
            traceMetadata,
            0,
            BuildContextLayersSnapshot(
                systemPrompt,
                memoryContext,
                executionPolicySystemPrompt,
                agentWorkspacePrompt,
                humanLoopSystemPrompt,
                clarificationSnapshot,
                (context.LastTranscriptSummary ?? string.Empty),
                sessionContext.RestoredMessages.Count,
                appendPrompt: pendingHumanReview is null,
                includeContinuationContext: !string.IsNullOrWhiteSpace(resumeState.ContinuationContext)
            )
        );

        // ── 6. AgentConfig + AgentRunState ────────────────────────────────────
        var agentConfig = new AgentConfig(
            prompt,
            responseSchema ?? string.Empty,
            allToolDefs,
            reusableToolNames,
            hilToolDefsByName,
            hilProxy.IsEnabled,
            runtimeParameters.ToolChoiceMode,
            runtimeParameters.AllowParallelToolCalls,
            requiresStructuredResponseFinalization,
            runtimeParameters.Temperature,
            runtimeParameters.MaxTokens,
            runtimeParameters.MaxIterations,
            runtimeParameters.ResponseQualityThreshold,
            resolvedPolicies,
            resolvedGuardrails,
            runtimeArguments,
            persistSession,
            hilProxy.AllowsHumanQuestions,
            turnConclusionTool
        );

        var agentRunState = new AgentRunState(sessionContext, messages, traceMetadata, executedToolCalls, new FailedToolTrackingState());
        agentRunState.ResumeState = resumeState;

        // ── 7. AgentRunServices ───────────────────────────────────────────────
        var ports = new AgentRunServices
        {
            InvokeLlm = (msgs, toolDefs, opts, innerCt) => runtimePort.InvokeLlmConversationAsync(msgs, toolDefs, opts, innerCt),
            RuntimePort = runtimePort,
            HilProxy = hilProxy.IsEnabled ? hilProxy : null,
            RunId = contextServices.RunId,
            CorrelationContext = contextServices.CorrelationContext,
            Checkpoint = contextServices.Checkpoint,
            SetState = contextServices.SetState,
            GetState = contextServices.GetState,
            UpdateTrace = contextServices.UpdateTrace,
            PublishStatus = contextServices.PublishStatus,
            LogDebug = contextServices.LogDebug,
            GuardrailWarningSink = guardrailWarnings,
        };

        // ── 8. Execute mode runtime ───────────────────────────────────────────
        if (finalResponse is null)
        {
            var result = await modeRuntime.ExecuteAsync(agentConfig, agentRunState, ports, this, ct).ConfigureAwait(false);
            finalResponse = result.Response;
            finishReason = result.FinishReason;
            executedIterations = result.ExecutedIterations;
        }

        // ── 9. Finalize response ──────────────────────────────────────────────
        // A turn that ended in a HIL suspension or a handoff legitimately has an empty
        // response: its user-facing content was delivered through the interaction channel
        // (interact_with_human blocks) or the handoff message — skip the empty-response
        // validation for those, it would otherwise fail the run. Same for a run that concluded
        // "done" after nothing but a send (or sends): its content already went out live, through
        // the send's own channel — see AgentRunState.DeliveredLiveContent's own doc comment for why
        // that content must never be duplicated into this response instead of just leaving it empty.
        var endedInSuspensionOrHandoff = agentRunState.Status == ExecutableStatus.WaitingFor || agentRunState.OrchestrationHint is not null || agentRunState.DeliveredLiveContent;
        if (!endedInSuspensionOrHandoff)
            finalResponse = AgentResponseFinalization.Resolve(finalResponse, finishReason, runtimeParameters.MaxIterations, contextServices.PublishStatus);

        return new AgentRunResult(finalResponse, finishReason, executedIterations)
        {
            FinalMessages = agentRunState.Messages,
            ExecutedToolCalls = agentRunState.ExecutedToolCalls.ToList(),
            TotalTokensUsed = agentRunState.TraceMetadata.BudgetState.TotalTokensUsed,
            OrchestrationHint = agentRunState.OrchestrationHint,
            OrchestrationAction = agentRunState.OrchestrationAction,
            OrchestrationNotes = agentRunState.OrchestrationNotes.ToList(),
            Status = agentRunState.Status,
            WaitingForToken = agentRunState.WaitingForToken,
            GuardrailWarnings = guardrailWarnings,
        };
    }

    /// <summary>
    /// Executes <see cref="RunAsync"/> followed by <see cref="IAgentSessionProxy.SaveAsync"/>.
    /// <para>
    /// This is the canonical core method shared by every shell (<c>AiNode</c>, <c>StandaloneAgent</c>,
    /// <c>TeamAgent</c> via <c>AgentExecutable</c>) — the only difference between shells is how
    /// the parameters and proxies are resolved/built before calling this method.
    /// </para>
    /// </summary>
    public async ValueTask<AgentRunResult> RunAgentAsync(
        string systemPrompt,
        string prompt,
        string responseSchema,
        string memoryContext,
        IAgenticModeRuntime modeRuntime,
        IReadOnlyDictionary<string, ArgumentDefinition> runtimeArguments,
        AgentRuntimeParameters runtimeParameters,
        ResolvedAgentPolicies resolvedPolicies,
        IReadOnlyList<AgentGuardrailDefinition> resolvedGuardrails,
        bool persistSession,
        SessionContext sessionContext,
        IAgentToolsProxy toolsProxy,
        IAgentSessionProxy sessionProxy,
        IAgentHilProxy hilProxy,
        IAgentTraceProxy traceProxy,
        IAgentLlmProxy runtimePort,
        AgentRunServices contextServices,
        RunContext context,
        CancellationToken ct = default,
        IReadOnlyList<ImageAttachment>? images = null
    )
    {
        ArgumentNullException.ThrowIfNull(sessionProxy);
        ArgumentNullException.ThrowIfNull(context);

        var agentResult = await RunAsync(systemPrompt, prompt, responseSchema, memoryContext, modeRuntime, runtimeArguments, runtimeParameters, resolvedPolicies, resolvedGuardrails, persistSession, sessionContext, toolsProxy, hilProxy, traceProxy, runtimePort, contextServices, context, ct, images)
            .ConfigureAwait(false);

        var persistedMessageCount = await sessionProxy.SaveAsync(sessionContext, agentResult.FinalMessages ?? [], prompt, agentResult.Response, agentResult.FinishReason, agentResult.ExecutedIterations, memoryContext, ct).ConfigureAwait(false);

        // Dispatch deferred HIL notifications (e.g. HilWaitProxy) now that the session
        // marker is durably saved — ensures the run can be resumed before the client replies.
        await hilProxy.PostRunAsync(ct).ConfigureAwait(false);

        // Folding this response into RunContext.LastTranscriptSummary (the team-shared channel)
        // is the orchestrator's job (HandoffGraphOrchestrator, after every completed member turn,
        // uniformly for every member type) — not this method's. See EPIC-session-summary-unification.md.

        return agentResult with
        {
            PersistedMessageCount = persistedMessageCount,
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a human-readable snapshot of the prompt context layers injected at a given iteration.
    /// Extracted from <c>WorkflowAgentExecutionPreparationCoordinator</c> — single source of truth.
    /// </summary>
    internal static string BuildContextLayersSnapshot(
        string systemPrompt,
        string memoryContext,
        string executionPolicySystemPrompt,
        string knowledgeBasePrompt,
        string humanLoopSystemPrompt,
        string clarificationLedgerSnapshot,
        string sessionSummary,
        int restoredMessageCount,
        bool appendPrompt,
        bool includeContinuationContext
    )
    {
        var layers = new List<string>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            layers.Add("1. system_prompt — base agent instructions and strongest runtime control layer.");

        if (!string.IsNullOrWhiteSpace(memoryContext))
            layers.Add($"{layers.Count + 1}. memory_context — external retrieved memory, support-only unless materially useful.");

        layers.Add($"{layers.Count + 1}. shared_execution_system_prompt — shared execution rules for the current runtime.");

        if (!string.IsNullOrWhiteSpace(executionPolicySystemPrompt))
            layers.Add($"{layers.Count + 1}. execution_policy_prompt — resolved policies and textual runtime rules applied generically to execution.");

        if (!string.IsNullOrWhiteSpace(knowledgeBasePrompt))
            layers.Add($"{layers.Count + 1}. agent_workspace_prompt — compressed structured support memory from previous turns.");

        if (!string.IsNullOrWhiteSpace(humanLoopSystemPrompt))
            layers.Add($"{layers.Count + 1}. human_loop_system_prompt — connected human-loop capabilities and interaction rules.");

        layers.Add($"{layers.Count + 1}. clarification_ledger_snapshot — resolved human clarifications retained for continuation.");

        if (!string.IsNullOrWhiteSpace(sessionSummary))
            layers.Add($"{layers.Count + 1}. session_summary — compressed earlier conversation history with weaker authority than recent conversation.");

        if (restoredMessageCount > 0)
            layers.Add($"{layers.Count + 1}. restored_messages ({restoredMessageCount}) — recent restored conversation turns kept above KB and summary.");

        if (includeContinuationContext)
            layers.Add($"{layers.Count + 1}. continuation_context — readable projection of the latest human input, weaker than recent conversation and the active ledger.");

        if (appendPrompt)
            layers.Add($"{layers.Count + 1}. current_user_prompt — active user task appended as the latest high-priority user turn.");

        var snapshot = string.Join(Environment.NewLine, layers);
        return string.IsNullOrWhiteSpace(clarificationLedgerSnapshot) ? snapshot : snapshot + Environment.NewLine + Environment.NewLine + "Clarification ledger snapshot:" + Environment.NewLine + clarificationLedgerSnapshot;
    }
}
