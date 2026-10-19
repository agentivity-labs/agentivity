using System.Text.Json;
using System.Text.Json.Nodes;
using Agentivity.Domain.Arguments;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Arguments;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Workflows.Interactions;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Correlation;
using Agentivity.Observability.Contracts.Events;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Concrete implementation of <see cref="IAgentManifest"/>.
/// Wraps <see cref="AgentConfig"/>, <see cref="AgentRunState"/>, <see cref="AgentRunServices"/>,
/// and <see cref="AgentHost"/> — all platform logic in one place, injected into the runtime.
///
/// Runtimes that implement <see cref="IAgenticModeRuntime.RunAsync"/> receive this object
/// and own their full execution loop. No coordinator, no hooks.
/// </summary>
public sealed class AgentManifest : IAgentManifest
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Steps.Implementations.AgentNextStepDecisionStep DecisionStep = new();

    private readonly AgentHost _agentHost;
    private readonly AgenticModeCapabilities _capabilities;
    private readonly AgentConfig _config;
    private readonly AgentRunState _state;
    private readonly AgentRunServices _ports;
    private readonly RunContext? _runContext;

    public AgentManifest(AgentHost agentHost, AgenticModeCapabilities capabilities, AgentConfig config, AgentRunState state, HumanReviewResumeState resumeState, AgentRunServices ports, RunContext? runContext = null, ExecutionCorrelationContext? correlation = null)
    {
        _agentHost = agentHost ?? throw new ArgumentNullException(nameof(agentHost));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _state.ResumeState = resumeState ?? HumanReviewResumeState.None;
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _runContext = runContext;
        _ = correlation; // kept for call-site compatibility; telemetry now flows through RunContext.Execution.Telemetry
    }

    // ── Suspension state ──────────────────────────────────────────────────────

    public bool IsRunning => _state.IsRunning;

    // ── Conversation ──────────────────────────────────────────────────────────

    public string Prompt => _config.Prompt;

    public IReadOnlyList<LlmChatMessage> Messages => _state.Messages;

    public LlmChatMessage? SystemMessage => _state.Messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));

    public void AddMessage(LlmChatMessage message) => _state.Messages.Add(message);

    // Some chat templates (Jinja-based, e.g. Qwen's) reject any "system" message that isn't the
    // very first one — so runtime-injected content (the ReAct loop prompt, human-loop rules, etc.)
    // is folded into the single leading system message instead of being inserted as its own entry.
    public void EnsureSystemPrompt(string systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
            return;

        var messages = _state.Messages;
        if (messages.Count > 0 && string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            var existing = messages[0].Content ?? string.Empty;
            if (existing.Contains(systemPrompt, StringComparison.Ordinal))
            {
                return;
            }

            messages[0] = messages[0] with { Content = string.IsNullOrEmpty(existing) ? systemPrompt : $"{existing}\n\n{systemPrompt}" };
            return;
        }

        messages.Insert(0, new LlmChatMessage("system", systemPrompt));
    }

    // ── LLM ───────────────────────────────────────────────────────────────────

    public double Temperature => _config.Temperature;
    public int? MaxTokens => _config.MaxTokens;
    public int? MaxIterations => _config.MaxIterations;

    public LlmToolChoiceMode ToolChoiceMode => _config.ToolChoiceMode;
    public bool AllowParallelToolCalls => _config.AllowParallelToolCalls;
    public string ResponseSchema => _config.ResponseSchema;
    public bool RequiresStructuredResponseFinalization => _config.RequiresStructuredResponseFinalization;

    public ValueTask<LlmConversationResponse> InvokeLlmAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options, CancellationToken cancellationToken) =>
        _ports.InvokeLlm is { } invoke ? invoke(messages, tools, options, cancellationToken) : throw new InvalidOperationException("No LLM invoker is wired for this agent run.");

    /// <summary>
    /// The agent's own per-iteration turn (<see cref="Steps.MainTurnStep"/>) — the counterpart to
    /// the decision step, using whatever tool set and parallel-call policy the calling runtime
    /// hands it. Mode runtimes call this instead of building <see cref="LlmConversationOptions"/>
    /// and calling <see cref="InvokeLlmAsync"/> directly, so that budget/schema resolution lives
    /// in one place (<see cref="Steps.Implementations.MainTurnStep.BuildOptions"/>) rather than
    /// copied per runtime.
    /// </summary>
    public ValueTask<LlmConversationResponse> RunMainTurnAsync(IReadOnlyList<JsonObject>? tools, bool allowParallelToolCalls, CancellationToken cancellationToken)
    {
        var step = new Steps.Implementations.MainTurnStep(tools, allowParallelToolCalls);
        var context = new Steps.AgentStepContext(_config, _state, iteration: 0, _runContext);
        return step.ExecuteAsync(context, InvokeLlmAsync, cancellationToken);
    }

    public async ValueTask RunSummaryIfDueAsync(CancellationToken cancellationToken)
    {
        // Constructed fresh, never shared/static — SummaryStep carries per-call state between
        // ResolveMessages and ApplyAsync (see its own doc comment).
        var step = new Steps.Implementations.SummaryStep();
        var context = new Steps.AgentStepContext(_config, _state, iteration: 0, _runContext);
        if (!step.ShouldRun(context))
            return;

        await step.ExecuteAsync(context, InvokeLlmAsync, cancellationToken).ConfigureAwait(false);
    }

    // ── Tools ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<JsonObject> ToolDefinitions => _config.ToolDefinitions;

    public IReadOnlyList<AgentToolExecution> ExecutedToolCalls => [.. _state.ExecutedToolCalls];

    /// <summary>
    /// Full platform tool execution: deduplication, HIL tool dispatch,
    /// observation sanitization, tracing. Appends tool messages to conversation.
    /// Equivalent to coordinator's ExecuteToolCallsAsync.
    /// </summary>
    public ValueTask<IReadOnlyList<AgentToolExecution>> ExecuteToolsAsync(IReadOnlyList<LlmToolCall> toolCalls, int iteration, CancellationToken cancellationToken) => ExecuteToolsCoreAsync(toolCalls, iteration, _state.Messages, scopedDeduplication: false, cancellationToken);

    /// <summary>
    /// Full platform tool execution with an explicit target message list.
    /// Tool messages are appended to <paramref name="targetMessages"/> instead of the
    /// shared <see cref="Messages"/> buffer, allowing work tasks to maintain isolated context.
    /// Deduplication is scoped to the current batch only — the global executed-tool snapshot
    /// is intentionally excluded so that parallel hermetic work tasks can legitimately call
    /// the same tool with the same arguments without triggering the infinite-loop guard.
    /// <see cref="ExecutedToolCalls"/> and failure tracking are still updated.
    /// </summary>
    public ValueTask<IReadOnlyList<AgentToolExecution>> ExecuteToolsAsync(IReadOnlyList<LlmToolCall> toolCalls, int iteration, List<LlmChatMessage> targetMessages, CancellationToken cancellationToken) =>
        ExecuteToolsCoreAsync(toolCalls, iteration, targetMessages, scopedDeduplication: true, cancellationToken);

    private async ValueTask<IReadOnlyList<AgentToolExecution>> ExecuteToolsCoreAsync(IReadOnlyList<LlmToolCall> toolCalls, int iteration, List<LlmChatMessage> messages, bool scopedDeduplication, CancellationToken cancellationToken)
    {
        var runtimePort = _ports.RuntimePort ?? throw new InvalidOperationException("The agent execution runtime port was not provided.");
        var executions = new List<AgentToolExecution>(toolCalls.Count);

        var knownToolNames = _config.ToolDefinitions.Select(LlmToolDefinitionJson.GetRequiredName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < toolCalls.Count; i++)
        {
            // Once a prior call in this same batch has already suspended (HIL ask), routed
            // (team handoff), or otherwise ended the turn, stop — dispatching the rest of the
            // batch would fire a second, conflicting suspension/routing decision. This matters
            // even though the runtime requests AllowParallelToolCalls: false, because some LLM
            // backends (e.g. LM Studio) don't always honor that and return several tool calls
            // (sometimes byte-identical duplicates) in a single response anyway.
            if (!_state.IsRunning)
                break;

            await PersistAgentStateAsync(cancellationToken).ConfigureAwait(false);
            var toolCall = toolCalls[i];

            // Turn-conclusion tool dispatch — record the agent's chosen outcome (done, needs_input,
            // or — team members only — a real handoff target) natively, with no tool-port wiring
            // (mirrors the HIL dispatch below). Matched by name against _config.RoutingToolDefinition
            // rather than _config.ToolDefinitions: this tool is deliberately never offered on the
            // agent's own turn (see IAgentToolsProxy.GetRoutingToolDefinition) — it only ever appears
            // here because a forced follow-up call (ResolveTurnConclusionAsync) produced this exact
            // call. Checked BEFORE the hallucinated-tool-name guard below: since this tool is
            // deliberately absent from _config.ToolDefinitions/knownToolNames, that guard would
            // otherwise reject every conclusion call as an unknown tool.
            var turnConclusionTool = _config.TurnConclusionTool is { } conclusionDef && string.Equals(LlmToolDefinitionJson.GetRequiredName(conclusionDef.Definition), toolCall.Name, StringComparison.OrdinalIgnoreCase) ? conclusionDef : null;
            if (turnConclusionTool is not null)
            {
                await DispatchRoutingToolCallAsync(toolCall, turnConclusionTool, messages, executions, iteration, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Hallucinated tool name guard — the LLM requested a tool that was never provided.
            if (!knownToolNames.Contains(toolCall.Name))
            {
                var knownList = string.Join(", ", knownToolNames);
                var observation = $"Tool '{toolCall.Name}' is not available. Available tools: {knownList}.";
                AppendTrace(new AgentTraceEvent(iteration, "hallucinated_tool_name", observation, []));
                throw new AgentDegradedException(AgenticFinishReason.HallucinatedToolName, $"I tried to use a tool called '{toolCall.Name}' that is not part of my available toolset. " + "Please try rephrasing your request or ask about the available capabilities.");
            }

            // Duplicate exact call guard — prevents infinite loops on broken tool calls.
            // When scopedDeduplication=true (hermetic work tasks) only the current batch is
            // checked; cross-task duplicate calls are legitimate and must not be blocked.
            if (ShouldBlockDuplicateExactToolCall(executions, toolCall, scopedDeduplication))
            {
                var blocked = new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, $"Duplicate tool call '{toolCall.Name}' with identical arguments blocked to prevent infinite loop.", true, false);
                executions.Add(blocked);
                messages.Add(new LlmChatMessage("tool", SanitizeObservation(blocked.ResultContent), ToolCallId: toolCall.Id));
                TraceToolExecution(iteration, blocked);
                continue;
            }

            // HIL tool dispatch — suspend for human review when tool is a HIL tool.
            if (_ports.HilProxy is not null && HumanLoopHelper.TryGetHumanLoopToolDefinition(toolCall, _config.EnableHumanLoop, _config.HumanLoopToolDefinitionsByName, out var humanLoopDef))
            {
                var remainingToolCalls = toolCalls.Skip(i + 1).ToList();
                var pendingReview = HumanLoopHelper.PendingHumanReviewForHumanLoopTool(iteration, toolCall, humanLoopDef!, remainingToolCalls, LogDebug);

                // No human-facing content at all (empty/missing content blocks, no question
                // argument) — there is nothing safe to show a human, so this is not a human-loop
                // interaction, it is a malformed tool call. Reject it like any other invalid call
                // (see the hallucinated-tool-name guard above): a tool-result error lets the model
                // see its own mistake and retry with real content or conclude normally, instead of
                // suspending the run on a fabricated, hardcoded, English "question" no one asked.
                if (pendingReview is null)
                {
                    var invalidResult = $"Invalid '{toolCall.Name}' call: no content was provided. Include actual text or widget content in the call, or omit it and answer directly.";
                    var invalidExecution = new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, invalidResult, true, false);
                    executions.Add(invalidExecution);
                    messages.Add(new LlmChatMessage("tool", SanitizeObservation(invalidExecution.ResultContent), ToolCallId: toolCall.Id));
                    TraceToolExecution(iteration, invalidExecution);
                    continue;
                }

                // Defense in depth: this agent's HIL tool schema is already built without an
                // "ask"/wait choice when AllowsHumanQuestions is false (see AgentProxyFactory /
                // ChatChannel.BuildInteractToolSchema), but a model can still emit an
                // out-of-schema value. Downgrade to Send rather than trust it — never let this
                // agent suspend the run for a reply.
                if (!_config.AllowsHumanQuestions && pendingReview.Operation == HumanInteractionOperation.SendAndWait)
                    pendingReview = pendingReview with { Operation = HumanInteractionOperation.Send };

                // "send" mode (e.g. interact_with_human called without waiting for a reply):
                // dispatch the content immediately and keep going — no suspension, no duplicate
                // guard (there's no question to compare against).
                if (pendingReview.Operation == HumanInteractionOperation.Send)
                {
                    // Safety net: a "send" whose text content actually awaits an answer is a
                    // missed "ask" — the model delivered a question without waiting for the
                    // reply. Requalify it to SendAndWait BEFORE dispatch (content is delivered
                    // unchanged, but the run suspends for the answer). Without this, the loop
                    // runs an extra turn, the model narrates "I sent you a question…", and the
                    // MissedAskHumanFallbackStep then wrongly surfaces that narration as the
                    // question. Widgets-only sends (e.g. a SummaryCard) have no text and its own
                    // ShouldRun skips them.
                    // Skipped entirely when this agent isn't allowed to ask the user anything at
                    // all (AgentConfig.AllowsHumanQuestions) — that flag already trims "ask" out of
                    // the tool's own schema, so a "send" here can never be legitimately requalified;
                    // reclassifying it anyway would silently re-introduce the very pause the flag
                    // exists to prevent.
                    if (_config.AllowsHumanQuestions)
                    {
                        var fallbackStep = new Steps.Implementations.MissedAskHumanFallbackStep(toolCall.ArgumentsJson);
                        var fallbackContext = new Steps.AgentStepContext(_config, _state, iteration, _runContext);
                        if (fallbackStep.ShouldRun(fallbackContext))
                        {
                            var needsHumanInput = await fallbackStep.ExecuteAsync(fallbackContext, InvokeLlmAsync, cancellationToken).ConfigureAwait(false);
                            Serilog.Log.Debug("[ChatDiag] MissedAskHumanFallback tool={ToolName} argsLen={ArgsLen} needsHumanInput={NeedsHumanInput}", toolCall.Name, toolCall.ArgumentsJson?.Length, needsHumanInput);
                            if (needsHumanInput)
                                pendingReview = pendingReview with { Operation = HumanInteractionOperation.SendAndWait };
                        }
                    }

                    Serilog.Log.Debug("[ChatDiag] AgentManifest send-dispatch tool={ToolName} operation={Operation} questionLen={QuestionLen} questionPreview={QuestionPreview}", toolCall.Name, pendingReview.Operation, pendingReview.Question?.Length, pendingReview.Question is { Length: > 0 } q ? q.Substring(0, Math.Min(120, q.Length)) : null);

                    if (pendingReview.Operation == HumanInteractionOperation.Send)
                    {
                        var sendResult = await _ports.HilProxy!.SendWithoutWaitingAsync(pendingReview, cancellationToken).ConfigureAwait(false);
                        _state.DeliveredLiveContent = true;
                        var sendExecution = new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, sendResult, false, false);
                        executions.Add(sendExecution);
                        messages.Add(new LlmChatMessage("tool", SanitizeObservation(sendExecution.ResultContent), ToolCallId: toolCall.Id));
                        TraceToolExecution(iteration, sendExecution);
                        // Note: a "send" only ever surfaces to the user through this tool call, never
                        // through the run's own final Response. If this turn later hands off with only
                        // a short transition note, this send's content isn't separately folded into
                        // the team-shared summary (RunContext.LastTranscriptSummary, updated by the
                        // orchestrator from TaskOutput.Content only) — a known simplification, not an
                        // oversight; see EPIC-session-summary-unification.md.
                        continue;
                    }
                }

                // Semantic duplicate guard — check whether the candidate HIL question is
                // semantically equivalent to a previously answered question. When the LLM
                // confirms a duplicate, inject a blocked tool result instead of suspending.
                var hilRunId =
                    _ports.RunId != Guid.Empty ? _ports.RunId
                    : _runContext?.RunId is { } rid && Guid.TryParse(rid, out var parsedRid) ? parsedRid
                    : Guid.Empty;
                var clarifications = HumanLoopConversationContextHelper.GetClarificationLedger(hilRunId);
                var blockedExecution = await TryBlockSemanticDuplicateHumanLoopAsync(toolCall, pendingReview, clarifications, cancellationToken).ConfigureAwait(false);
                if (blockedExecution is not null)
                {
                    executions.Add(blockedExecution);
                    messages.Add(new LlmChatMessage("tool", SanitizeObservation(blockedExecution.ResultContent), ToolCallId: toolCall.Id));
                    TraceToolExecution(iteration, blockedExecution);
                    continue;
                }

                var hilExecution = new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, string.Empty, false, false);
                AppendTrace(new AgentTraceEvent(iteration, toolCall.Name, null, [hilExecution]));
                var hilToken = await _ports.HilProxy!.WaitForHumanReviewAsync(_state.TraceMetadata, _state.SessionContext, messages, pendingReview, _state.ExecutedToolCalls.Snapshot().Concat(executions).ToList(), _config.PersistSession, cancellationToken).ConfigureAwait(false);
                if (hilToken is not null)
                {
                    _state.Status = ExecutableStatus.WaitingFor;
                    _state.WaitingForToken = hilToken;
                    _state.IsRunning = false;
                }
                continue;
            }

            // Standard tool execution.
            // Any exception that is not a cancellation propagates immediately — the tool
            // has thrown unexpectedly and the agent must stop. The exception message is
            // recorded in the trace before re-throwing so post-mortem debugging is possible.
            try
            {
                var execution = await runtimePort.ExecuteToolCallAsync(_state.SessionContext, _config.ReusableToolNames, toolCall, cancellationToken).ConfigureAwait(false);

                // STORY-0422.4 — Post-call validation: empty/null or business-error responses
                // are surfaced as structured observations rather than silently forwarded.
                execution = NormalizeToolExecution(execution, toolCall.Name);

                executions.Add(execution);
                messages.Add(new LlmChatMessage("tool", SanitizeObservation(execution.ResultContent), ToolCallId: toolCall.Id));
                TraceToolExecution(iteration, execution);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentDegradedException)
            {
                // Already a structured degraded signal — propagate as-is.
                throw;
            }
            catch (Exception ex)
            {
                // Trace the failure for diagnostics, then surface as a degraded result
                // so the user receives a readable explanation instead of a raw stack trace.
                var observation = ToolFailureObservationFormatter.FormatException(toolCall.Name, ex);
                AppendTrace(new AgentTraceEvent(iteration, "tool_exception", observation, []));
                throw new AgentDegradedException(AgenticFinishReason.ToolCallFailed, $"A technical error occurred while running '{toolCall.Name}': {DescribeToolException(ex)}", ex);
            }

            await PersistAgentStateAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var execution in executions)
            _state.ExecutedToolCalls.Add(execution);
        AgentToolFailureTracker.RegisterFailures(executions, _state.FailedToolState.FailedAttemptsBySignature, _state.FailedToolState.FailedAttempts, iteration);
        return executions;
    }

    /// <summary>
    /// The reason a tool threw, in one line for the person reading the run's error: the innermost exception's own message
    /// ("The process cannot access the file … because it is being used by another process"), never a stack trace.
    /// </summary>
    public static string DescribeToolException(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        var line = message.Split('\n', 2)[0].Trim();
        if (line.Length == 0)
            line = ex.GetBaseException().GetType().Name;
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    /// <summary>
    /// Records the agent's chosen turn-conclusion outcome — done, needs_input, or (team members
    /// only) a real handoff target — from an <c>agent_next_step</c> call, whether it came from the
    /// model's own turn or the decision step in <see cref="ResolveTurnConclusionAsync"/>. The
    /// delivered text is always <see cref="AgentRunState.PendingTurnDraft"/> — the tool's schema
    /// has no text field of its own, so there is only ever one possible source.
    /// </summary>
    private async ValueTask DispatchRoutingToolCallAsync(LlmToolCall toolCall, ITurnConclusionTool turnConclusionTool, List<LlmChatMessage> messages, List<AgentToolExecution> executions, int iteration, CancellationToken cancellationToken)
    {
        var outcome = turnConclusionTool.Interpret(toolCall.ArgumentsJson);

        // Best-effort side information that rides along with ANY decision: recorded, never acted on —
        // it can neither block nor change the decision made.
        foreach (var note in outcome.Notes ?? [])
        {
            if (!_state.OrchestrationNotes.Contains(note, StringComparer.OrdinalIgnoreCase))
                _state.OrchestrationNotes.Add(note);
        }

        // This tool_use's own tool_result must be appended BEFORE any branch below that might
        // itself append further messages (needs_input's HIL suspension adds a synthetic
        // interact_with_human tool_use + a resume marker). Providers with strict message-shape
        // validation (Anthropic) reject a tool_use that isn't immediately followed by its
        // tool_result — appending this after the branch put the synthetic call's messages in
        // between, and since HIL resume later strips the marker "and everything after it" to
        // regenerate the turn, this tool_result (added last) was stripped right along with it,
        // permanently losing the pairing on every subsequent request. Confirmed in production
        // against Anthropic's Messages API (LM Studio/OpenAI-compatible backends don't enforce
        // this adjacency, which is why it never surfaced there).
        var routingExecution = new AgentToolExecution(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson, outcome.ToolResult, false, false);
        executions.Add(routingExecution);
        messages.Add(new LlmChatMessage("tool", SanitizeObservation(routingExecution.ResultContent), ToolCallId: toolCall.Id));
        TraceToolExecution(iteration, routingExecution);

        switch (outcome.Kind)
        {
            case TurnConclusionKind.NeedsInput:
                await DispatchNeedsInputAsync(toolCall, iteration, cancellationToken).ConfigureAwait(false);
                break;

            case TurnConclusionKind.Conclude:
                // The turn ends: either an explicit "nothing more to do" (the run finalizes as an
                // ordinary completed turn, but as a decision the model actually made rather than one
                // inferred from a silent plain-text answer), or a hand-off to the orchestrator carrying
                // a protocol-defined action/target the engine does not interpret.
                _state.IsRunning = false;
                if (outcome.AlwaysDeliverDraft)
                    _state.HandoffMessage = _state.PendingTurnDraft ?? string.Empty;
                else if (!string.IsNullOrWhiteSpace(_state.PendingTurnDraft))
                    _state.HandoffMessage = _state.PendingTurnDraft;
                _state.PendingTurnDraft = null;
                if (outcome.Target is not null)
                    _state.OrchestrationHint = outcome.Target;
                if (outcome.Action is not null)
                    _state.OrchestrationAction = outcome.Action;
                break;
        }
    }

    /// <summary>
    /// Handles the turn-conclusion tool's <c>needs_input</c> outcome — the generic, HIL-agnostic
    /// counterpart of <c>done</c>. If this agent has a usable HIL channel, suspends for a real
    /// human reply (same mechanics as an explicit <c>interact_with_human</c> call — see
    /// <c>HumanLoopHelper.CreateHumanLoopToolCall</c>'s synthetic-call rationale). Otherwise there
    /// is nothing that can wait for an answer, so it degrades exactly like <c>done</c>: finalize
    /// with the question as the final text rather than getting stuck.
    /// </summary>
    private async ValueTask DispatchNeedsInputAsync(LlmToolCall toolCall, int iteration, CancellationToken cancellationToken)
    {
        var question = _state.PendingTurnDraft ?? string.Empty;
        _state.PendingTurnDraft = null;

        if (_ports.HilProxy is { } hilProxy && HumanLoopHelper.SupportsFreeTextHumanLoop(_config.EnableHumanLoop, _config.HumanLoopToolDefinitionsByName))
        {
            var freeTextTool = _config.HumanLoopToolDefinitionsByName.Values.FirstOrDefault(tool => AgentHumanLoopToolDefinitionJson.GetResultMode(tool) == AgentHumanLoopResultMode.FreeText);
            var hilToolCallId = Guid.NewGuid().ToString("N");
            var (fallbackToolName, fallbackArgumentsJson) = freeTextTool is not null
                ? (LlmToolDefinitionJson.GetRequiredName(freeTextTool), HumanLoopHelper.CreateHumanLoopToolCall(freeTextTool, [], question, null, null, hilToolCallId).ArgumentsJson)
                : ("ask_human", "{}");
            var pendingReview = new PendingHumanReview(
                AgentHumanReviewKind.AskHuman,
                Guid.NewGuid(),
                iteration,
                "Clarification needed",
                question,
                [],
                null,
                question,
                hilToolCallId,
                fallbackToolName,
                fallbackArgumentsJson,
                InteractionKind.Input,
                HumanInteractionOperation.SendAndWait,
                [],
                AgentHumanLoopResultMode.FreeText,
                [],
                [],
                DateTimeOffset.UtcNow
            );

            var hilToken = await hilProxy.WaitForHumanReviewAsync(_state.TraceMetadata, _state.SessionContext, _state.Messages, pendingReview, [.. _state.ExecutedToolCalls.Snapshot()], _config.PersistSession, cancellationToken).ConfigureAwait(false);
            if (hilToken is not null)
            {
                _state.Status = ExecutableStatus.WaitingFor;
                _state.WaitingForToken = hilToken;
                _state.IsRunning = false;
            }
            // else: a blocking proxy already resolved the answer in-place — loop continues normally.
        }
        else
        {
            // No usable HIL channel — can't suspend. Degrade like "done": finalize with the question as final text.
            _state.IsRunning = false;
            _state.HandoffMessage = string.IsNullOrWhiteSpace(question) ? string.Empty : question;
        }
    }

    // ── Policies ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps <see cref="AgentPolicyContext"/> keys to policy rules.
    /// Delegates to <see cref="IAgentPolicyPort"/> when wired via <see cref="RunContext"/>,
    /// otherwise falls back to <see cref="IAgentPolicyRuntime"/> with the resolved policies.
    /// </summary>
    public IReadOnlyList<string> GetPolicyRules(string context)
    {
        if (_runContext?.Policy is IAgentPolicyPort policyPort)
            return policyPort.GetRules(context);

        return _agentHost.PolicyRuntime.GetPolicyRules(context, _config.ResolvedPolicies);
    }

    // ── Guardrails ────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps <see cref="AgentGuardrailContext"/> keys to <see cref="AgentGuardrailStage"/>.
    /// Delegates to <see cref="IAgentGuardrailPort"/> when wired via <see cref="RunContext"/>,
    /// otherwise falls back to <see cref="AgentHost.ApplyGuardrails"/> with the resolved guardrails.
    /// </summary>
    public async ValueTask<string> ApplyGuardrailsAsync(string stage, string content, CancellationToken cancellationToken = default)
    {
        if (_runContext?.Guardrail is IAgentGuardrailPort guardrailPort)
            return await guardrailPort.ApplyAsync(stage, content, _ports.GuardrailWarningSink, cancellationToken).ConfigureAwait(false);

        var guardrailStage = stage switch
        {
            AgentGuardrailContext.ExecutionPrompt => AgentGuardrailStage.ExecutionPrompt,
            AgentGuardrailContext.TaskBatchPrompt => AgentGuardrailStage.TaskBatchPrompt,
            AgentGuardrailContext.JoinPrompt => AgentGuardrailStage.JoinPrompt,
            AgentGuardrailContext.ReplanningPrompt => AgentGuardrailStage.ReplanningPrompt,
            AgentGuardrailContext.FinalResponse => AgentGuardrailStage.FinalResponse,
            AgentGuardrailContext.HumanLoopPrompt => AgentGuardrailStage.HumanLoopPrompt,
            AgentGuardrailContext.ResponseQualityAssessmentPrompt => AgentGuardrailStage.ResponseQualityAssessmentPrompt,
            _ => (AgentGuardrailStage?)null,
        };

        if (guardrailStage is null)
            return content;

        return await _agentHost.ApplyGuardrailsAsync(_config.ResolvedGuardrails, guardrailStage.Value, content, allowNullOrEmptyContent: false, InvokeLlmAsync, cancellationToken, _ports.GuardrailWarningSink).ConfigureAwait(false) ?? content;
    }

    // ── Platform services ─────────────────────────────────────────────────────

    public async ValueTask PersistAgentStateAsync(CancellationToken cancellationToken)
    {
        // Workflow checkpoint (node-level persistence via AgentRunServices.Checkpoint delegate).
        if (_ports.Checkpoint is { } ck)
            await ck(cancellationToken).ConfigureAwait(false);

        // Session checkpoint — persists current messages so a crash/HIL can resume from here.
        // IRunSessionPort is the contracts-layer base; IAgentSessionPort is the full port with CheckpointAsync.
        if (_runContext?.Session is IAgentSessionPort sessionPort)
            await sessionPort.CheckpointAsync(_state.Messages, _state.SessionContext.TranscriptUpToIndex, cancellationToken).ConfigureAwait(false);
    }

    public void SetScratchpad<T>(string key, T data)
        where T : class
    {
        var json = JsonSerializer.SerializeToNode(data, SerializerOptions);
        _ports.SetState?.Invoke(key, json);
        if (_state.SessionContext.Enabled && _state.SessionContext.State.Metadata is { } meta)
            meta[key] = json;
    }

    public bool TryRestoreScratchpad<T>(string key, out T? data)
        where T : class
    {
        object? raw = null;

        if (_state.SessionContext.Enabled && _state.SessionContext.State.Metadata is { } meta && meta.TryGetValue(key, out raw) && raw is not null)
        {
            // found in session metadata
        }
        else if (_ports.GetState is not { } getState)
        {
            data = null;
            return false;
        }
        else
        {
            var (found, stateRaw) = getState(key);
            if (!found || stateRaw is null)
            {
                data = null;
                return false;
            }
            raw = stateRaw;
        }

        try
        {
            data = raw switch
            {
                T typed => typed,
                System.Text.Json.Nodes.JsonNode node => node.Deserialize<T>(SerializerOptions),
                System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.Object => System.Text.Json.JsonSerializer.Deserialize<T>(el.GetRawText(), SerializerOptions),
                string json when !string.IsNullOrWhiteSpace(json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, SerializerOptions),
                _ => null,
            };
            return data is not null;
        }
        catch (System.Text.Json.JsonException)
        {
            data = null;
            return false;
        }
    }

    /// <summary>
    /// See <see cref="IAgentManifest.DispatchLiveContentAsync"/>. Reuses the exact same delivery
    /// path as an explicit <c>interact_with_human(mode: "send")</c> tool call — a synthetic
    /// single-text-block payload dispatched through <see cref="IAgentHilProxy.SendWithoutWaitingAsync"/>
    /// — so it renders identically (same chat bubble mechanics) without adding any new channel code.
    /// </summary>
    public async ValueTask DispatchLiveContentAsync(string content, IReadOnlyList<LlmToolCall> concurrentToolCalls, int iteration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content) || !_config.EnableHumanLoop || _ports.HilProxy is not { } hilProxy)
            return;

        // A human-loop or routing tool call already delivers its own content through its dedicated
        // channel (the tool call's own content blocks / message field) — dispatching `content` here
        // too would show it a second time, as a separate, redundant message (observed: it broke
        // widget delivery for `interact_with_human` calls, whose accompanying `content` is typically
        // just a plain-language paraphrase of the same question already carried by the call itself).
        var isHumanLoopOrRoutingCall = concurrentToolCalls.Any(tc =>
            HumanLoopHelper.TryGetHumanLoopToolDefinition(tc, _config.EnableHumanLoop, _config.HumanLoopToolDefinitionsByName, out _) ||
            (_config.TurnConclusionTool is { } conclusionDef && string.Equals(LlmToolDefinitionJson.GetRequiredName(conclusionDef.Definition), tc.Name, StringComparison.OrdinalIgnoreCase)));
        if (isHumanLoopOrRoutingCall)
            return;

        var humanLoopToolDefinition = _config.HumanLoopToolDefinitionsByName.Values.FirstOrDefault(tool => AgentHumanLoopToolDefinitionJson.GetResultMode(tool) == AgentHumanLoopResultMode.FreeText);

        // Some models (small/local ones especially) occasionally write out a tool call as plain
        // prose — `{"name": "interact_with_human", "arguments": {...}}` — instead of actually
        // invoking it. Left alone, that raw JSON would be delivered verbatim as the chat message.
        // Re-synthesize it as the real call it was clearly trying to make so it dispatches through
        // the normal widget pipeline instead. Always forced to "send": this path never suspends the
        // turn, so honoring a fabricated "ask" here would claim to wait without actually gating
        // anything.
        var pendingReview = HumanLoopHelper.TryReparseHallucinatedToolCallContent(content, _config.HumanLoopToolDefinitionsByName, out var reparsedToolCall, out var reparsedDefinition)
            ? (HumanLoopHelper.PendingHumanReviewForHumanLoopTool(iteration, reparsedToolCall!, reparsedDefinition!, [], LogDebug) is { } reparsedReview
                ? reparsedReview with { Operation = HumanInteractionOperation.Send }
                : HumanLoopHelper.BuildPlainTextPendingReview(humanLoopToolDefinition, content, iteration))
            : HumanLoopHelper.BuildPlainTextPendingReview(humanLoopToolDefinition, content, iteration);

        await hilProxy.SendWithoutWaitingAsync(pendingReview, cancellationToken).ConfigureAwait(false);
        _state.DeliveredLiveContent = true;
    }

    public async ValueTask<AgentRunResult> FinalizeAsync(string response, string finishReason, int executedIterations, CancellationToken cancellationToken)
    {
        // A turn that ended in a handoff, an explicit "done" conclusion (also carried via
        // HandoffMessage, with OrchestrationHint left null — see the routing dispatch above), or a
        // HIL suspension delivers its user-facing text exclusively through the dedicated channel
        // (team_handoff's `message` field, or interact_with_human's content blocks, or — when
        // content accompanied a tool call — DispatchLiveContentAsync, already called earlier this
        // same iteration). Any assistant `content` reaching this point alongside those tool calls is
        // internal reasoning ("thinking aloud", common with small models) and must never surface
        // as a chat message a second time.
        if (_state.OrchestrationHint is not null || _state.HandoffMessage is not null)
            return new AgentRunResult(_state.HandoffMessage ?? string.Empty, finishReason, executedIterations);

        if (_state.Status == ExecutableStatus.WaitingFor)
            return new AgentRunResult(string.Empty, finishReason, executedIterations);

        if (string.IsNullOrWhiteSpace(response))
        {
            return new AgentRunResult(string.Empty, AgenticFinishReason.MaxIterations, executedIterations);
        }

        var finalResponse = response;

        // Optional quality refinement.
        var qualityThreshold = _config.ResponseQualityThreshold;
        if (qualityThreshold > 0 && !_config.RequiresStructuredResponseFinalization)
        {
            await PersistAgentStateAsync(cancellationToken).ConfigureAwait(false);
            finalResponse = await TryRefineResponseQualityAsync(finalResponse, executedIterations, cancellationToken).ConfigureAwait(false);
        }

        // Apply final guardrail.
        finalResponse = await ApplyGuardrailsAsync(AgentGuardrailContext.FinalResponse, finalResponse, cancellationToken).ConfigureAwait(false);

        return new AgentRunResult(finalResponse, finishReason, executedIterations);
    }

    public async ValueTask<AgentRunResult?> TryFinalizeTurnAsync(LlmConversationResponse response, int iteration, CancellationToken cancellationToken)
    {
        SetLatestResponse(response.Content ?? string.Empty);
        AddMessage(new LlmChatMessage("assistant", (response.Content ?? string.Empty).Trim(), ToolCalls: response.ToolCalls.Count > 0 ? response.ToolCalls : null));

        if (response.ToolCalls.Count == 0)
            return await FinalizeAsync(response.Content ?? string.Empty, response.FinishReason, iteration, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(response.Content))
            await DispatchLiveContentAsync(response.Content, response.ToolCalls, iteration, cancellationToken).ConfigureAwait(false);

        await ExecuteToolsAsync(response.ToolCalls, iteration, cancellationToken).ConfigureAwait(false);

        // A turn that only communicated (send-mode human-loop calls) owes the same explicit
        // decision as one that ended in plain text (see AgentNextStepDecisionStep.ShouldRun) — but
        // AFTER its sends were delivered, as a separate call that now sees them and their results
        // in the conversation. Skipped when executing them already ended the turn (a send
        // requalified to an "ask" suspended the run).
        //
        // MainTurnDraft is deliberately left UNSET here — do not "helpfully" wire response.Content
        // into it again (a regression once shipped, and reverted after production evidence below).
        // Every turn reaching this branch, by ShouldRun's own precondition, had response.ToolCalls
        // consisting ONLY of send-mode human-loop calls — which means DispatchLiveContentAsync,
        // already called above, unconditionally skipped delivering response.Content live (see its
        // own comment: text accompanying a HIL/routing call is disposable narration, the call's own
        // content is the substance). Setting MainTurnDraft here fed that same disposable text into
        // DispatchRoutingToolCallAsync's isDone/isValidTargetedAction branches, which unconditionally
        // copy it into HandoffMessage/AgentRunResult.Response — delivered, once more, only at the very
        // end of the whole run (AgentExecutable's final-response publish), i.e. chronologically AFTER
        // the send it was meant to introduce. Confirmed in production: a Visa specialist's turn wrote
        // "Excellent ! ... Voici les informations essentielles :" alongside a send carrying the actual
        // StatusCard — the caption meant to lead into the card was instead delivered as a second,
        // trailing bubble after it, reversing the model's own intended reading order. FinalizeAsync's
        // own doc comment already states the correct invariant: content accompanying a HIL/routing
        // call "must never surface as a chat message a second time" — this is that invariant, restored.
        if (IsRunning)
        {
            var decisionContext = new Steps.AgentStepContext(_config, _state, iteration, _runContext) { TurnResponse = response, AfterDeliveredSends = true };
            if (DecisionStep.ShouldRun(decisionContext))
            {
                var decision = await DecisionStep.ExecuteAsync(decisionContext, InvokeLlmAsync, cancellationToken).ConfigureAwait(false);
                if (decision.ToolCalls.Count > 0)
                {
                    AddMessage(new LlmChatMessage("assistant", (decision.Content ?? string.Empty).Trim(), ToolCalls: decision.ToolCalls));
                    await ExecuteToolsAsync(decision.ToolCalls, iteration, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return IsRunning ? null : await FinalizeAsync(response.Content ?? string.Empty, response.FinishReason, iteration, cancellationToken).ConfigureAwait(false);
    }

    public string? ReplySchema
    {
        get
        {
            if (_config.RequiresStructuredResponseFinalization)
                return null;

            return string.IsNullOrWhiteSpace(_config.ResponseSchema) ? null : _config.ResponseSchema;
        }
    }

    /// <summary>
    /// Called once the main per-turn call has produced a plain-text response with no tool call
    /// (a turn whose tool calls are all <c>send</c>-mode human-loop calls owes the same decision,
    /// but only once those sends were executed — see <see cref="TryFinalizeTurnAsync"/>; this
    /// method never touches a response that carries tool calls, so it can never discard one).
    /// Runs the decision step: a single follow-up call restricted to <c>agent_next_step</c>
    /// (<see cref="AgentConfig.RoutingToolDefinition"/> — always populated, see
    /// <see cref="IAgentToolsProxy.GetRoutingToolDefinition"/>) so the agent states what happens
    /// next explicitly — "done", "needs_input", or (team members only) a real handoff target —
    /// instead of trailing off in free text. This step's schema carries no text field (see
    /// <see cref="Routing.AgentNextStepTool"/>): the reply the user actually sees is, and always
    /// remains, this main turn's own <paramref name="response"/> — remembered in
    /// <see cref="AgentRunState.PendingTurnDraft"/> for <see cref="DispatchRoutingToolCallAsync"/>
    /// to use once the decision resolves. <see cref="ExecuteToolsCoreAsync"/>'s routing-tool
    /// dispatch (matched against <c>_config.RoutingToolDefinition</c>, before the
    /// hallucinated-tool-name guard) interprets the decision.
    /// </summary>
    public async ValueTask<LlmConversationResponse> ResolveTurnConclusionAsync(LlmConversationResponse response, int iteration, CancellationToken cancellationToken)
    {
        var context = new Steps.AgentStepContext(_config, _state, iteration, _runContext) { TurnResponse = response };
        if (!DecisionStep.ShouldRun(context))
            return response;

        // The decision step must see the reply it's being asked about — add it now rather than
        // waiting for TryFinalizeTurnAsync (which only runs after this call returns, once with
        // the decision step's own response, never with this original one).
        var draft = response.Content!.Trim();
        AddMessage(new LlmChatMessage("assistant", draft));

        context = new Steps.AgentStepContext(_config, _state, iteration, _runContext) { TurnResponse = response, MainTurnDraft = draft };
        return await DecisionStep.ExecuteAsync(context, InvokeLlmAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// This agent's own most recent finalized turn text — the last plain-text (no tool call)
    /// assistant message in its own running conversation, or null on its first turn. Used to give
    /// an isolated safety-net call (which otherwise sees no conversation history of its own)
    /// something to compare a new draft against when checking for self-repetition. Sourced from
    /// <see cref="AgentRunState.Messages"/> directly (this agent's own, always-populated
    /// conversation) rather than the team-shared <c>RunContext.LastTranscriptSummary</c>, which
    /// isn't structured per-author.
    /// </summary>
    private string? GetPreviousOwnTurnText() =>
        _state.Messages
            .LastOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) && (m.ToolCalls is null || m.ToolCalls.Count == 0) && !string.IsNullOrWhiteSpace(m.Content))
            ?.Content;

    public bool SupportsFreeTextHumanLoop => HumanLoopHelper.SupportsFreeTextHumanLoop(_config.EnableHumanLoop, _config.HumanLoopToolDefinitionsByName);

    /// <summary>
    /// Resolves human-loop suspension for a dedicated, standalone call that the caller itself built
    /// with <see cref="AgentSchemas.ConversationalReply"/> and no tools (e.g. a synthesis or work-task
    /// fallback call, see <c>LlmWorkTask</c>/<c>PlanExecuteModeRuntime</c>) — a separate, narrower
    /// path than <see cref="ResolveTurnConclusionAsync"/> (used by the 3 main mode runtimes): no
    /// auto-detection is needed here because the caller already knows which schema it used.
    /// </summary>
    public async ValueTask<LlmConversationResponse> ResolveMergedHumanLoopReplyAsync(LlmConversationResponse response, int iteration, CancellationToken cancellationToken)
    {
        if (!SupportsFreeTextHumanLoop)
            return response;

        var reply = HumanLoopHelper.ParseConversationalReply(response.Content);
        response = response with { Content = reply.Content };

        return await ApplyHumanLoopDecisionAsync(response, reply.NeedsHumanInput, reply.IsSelectionQuestion, reply.IsRepeatOfPreviousTurn, iteration, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<LlmConversationResponse> ApplyHumanLoopDecisionAsync(LlmConversationResponse response, bool needsHumanInput, bool isSelectionQuestion, bool isRepeatOfPreviousTurn, int iteration, CancellationToken cancellationToken)
    {
        response = response with { NeedsHumanInput = needsHumanInput, IsSelectionQuestion = isSelectionQuestion };

        // A team member's turn that ends in plain text owes the team an explicit routing decision —
        // either the agent's answer is complete and it's about to silently stop (needsHumanInput
        // false), or it's about to ask essentially the same question it already asked on its own
        // previous turn (a stall that would just continue the loop that produced it). Both cases
        // force a single constrained follow-up call restricted to the routing tool so the agent must
        // say who's next (a teammate, or "done") instead of trailing off. Scoped to genuine team
        // members (HasRealTargets) — every agent now carries a RoutingToolDefinition by default
        // (see IAgentToolsProxy.GetRoutingToolDefinition), but forcing it here for a non-team agent
        // would just be an unrequested extra call; ResolveTurnConclusionAsync is the generic
        // counterpart for the 3 main mode runtimes. If the model still doesn't produce a call, this
        // falls through to the normal handling below rather than getting stuck.
        if ((isRepeatOfPreviousTurn || !needsHumanInput) && _config.TurnConclusionTool is { OffersOrchestration: true })
        {
            // Same AgentNextStepDecisionStep as ResolveTurnConclusionAsync — a tiny structured
            // routing_id/context call never needs the agent's own MaxTokens, and capping it makes
            // a non-compliant model fail fast. MainTurnDraft is deliberately left unset here: this
            // forced follow-up never remembered a PendingTurnDraft, unlike the generic path.
            var context = new Steps.AgentStepContext(_config, _state, iteration, _runContext) { TurnResponse = response };
            var forcedResponse = await DecisionStep.ExecuteAsync(context, InvokeLlmAsync, cancellationToken).ConfigureAwait(false);
            if (forcedResponse.ToolCalls.Count > 0)
                return forcedResponse with { NeedsHumanInput = false, IsSelectionQuestion = false };
        }

        if (!needsHumanInput || string.IsNullOrWhiteSpace(response.Content))
            return response;

        var decision = _agentHost.PolicyRuntime.EvaluateHumanInputPolicy(needsHumanInput, isSelectionQuestion, _config.ResolvedPolicies);
        if (!decision.ShouldInteractWithHuman)
            return response;

        if (!HumanLoopHelper.SupportsFreeTextHumanLoop(_config.EnableHumanLoop, _config.HumanLoopToolDefinitionsByName))
            return response;

        // No real tool call was made — the model answered in plain text and the safety net
        // flagged it as needing a human reply. Build the pending review directly from that text,
        // but the synthetic assistant tool-call HilWaitProxy/WorkflowHilProxy inject to keep the
        // conversation structurally valid for the next turn must carry REAL arguments, not "{}":
        // an empty call, replayed back to the model on resume with no other example of this tool's
        // shape in context, reads as "I called this tool with nothing" — confirmed in production to
        // push the model into simulating tool calls as raw text on the very next turn instead of
        // making a native call (it has no valid in-context example left to imitate). The tool's own
        // `content` schema always accepts a plain text block, so a real, valid call is always
        // constructible here — CreateHumanLoopToolCall already does exactly this for the identical
        // "safety-net has only free text" case elsewhere (semantic-duplicate re-ask).
        var freeTextTool = _config.HumanLoopToolDefinitionsByName.Values.FirstOrDefault(tool => AgentHumanLoopToolDefinitionJson.GetResultMode(tool) == AgentHumanLoopResultMode.FreeText);
        var toolCallId = Guid.NewGuid().ToString("N");
        var (fallbackToolName, fallbackArgumentsJson) = freeTextTool is not null
            ? (LlmToolDefinitionJson.GetRequiredName(freeTextTool), HumanLoopHelper.CreateHumanLoopToolCall(freeTextTool, [], response.Content, null, null, toolCallId).ArgumentsJson)
            : ("ask_human", "{}");
        var pendingReview = new PendingHumanReview(
            AgentHumanReviewKind.AskHuman,
            Guid.NewGuid(),
            iteration,
            "Clarification needed",
            response.Content,
            [],
            null,
            response.Content,
            toolCallId,
            fallbackToolName,
            fallbackArgumentsJson,
            InteractionKind.Input,
            HumanInteractionOperation.SendAndWait,
            [],
            AgentHumanLoopResultMode.FreeText,
            [],
            [],
            DateTimeOffset.UtcNow
        );

        if (_ports.HilProxy is { } hilProxy)
        {
            var hilToken = await hilProxy.WaitForHumanReviewAsync(_state.TraceMetadata, _state.SessionContext, _state.Messages, pendingReview, [.. _state.ExecutedToolCalls.Snapshot()], _config.PersistSession, cancellationToken).ConfigureAwait(false);
            if (hilToken is not null)
            {
                _state.Status = ExecutableStatus.WaitingFor;
                _state.WaitingForToken = hilToken;
                _state.IsRunning = false;
            }
        }

        return response;
    }

    // ── Resume state ──────────────────────────────────────────────────────────

    public HumanReviewResumeState ResumeState => _state.ResumeState;

    // ── Tracing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Traces one tool call the agent made — whatever came of it (done, refused, sent to a person). ReAct traces it as the
    /// observation of its action; otherwise it is the call itself, with what it asked and what it got back, as a call that waits
    /// for a person is.
    /// </summary>
    private void TraceToolExecution(int iteration, AgentToolExecution execution)
    {
        if (_capabilities.EmitsReActStepTrace)
            AppendTrace(new AgentTraceEvent(iteration, "react_observation", execution.ResultContent, []));
        else
            AppendTrace(new AgentTraceEvent(iteration, execution.ToolName, null, [execution]));
    }

    public void AppendTrace(TraceEvent traceEvent)
    {
        // Write to the platform trace accumulator when wired (workflow path).
        _ports.UpdateTrace?.Invoke(accumulator =>
        {
            AgentExecutionTraceRootHelper.EnsureExecutionRoot(accumulator, _state.TraceMetadata, traceEvent.Iteration);
            accumulator.Append(traceEvent.ToDebugTraceItem());
        });

        // Also forward to the chain-level execution trace port when wired.
        _runContext?.Execution?.Trace?.Append(source: _runContext.EntityId, kind: traceEvent.GetType().Name, message: traceEvent.ToDebugTraceItem().Message ?? string.Empty, iteration: traceEvent.Iteration);
    }

    public void SetLatestResponse(string content)
    {
        // Content is always accessible via the latest assistant message — no separate field needed.
    }

    public void PublishStatusMessage(string message) => _ports.PublishStatus?.Invoke(message);

    public void LogDebug(string message) => _ports.LogDebug?.Invoke(message);

    public IAgentTelemetry? Telemetry
    {
        get
        {
            if (_ports.TelemetryEmitter is { } emitter && _ports.CorrelationContext is { } correlation)
                return new EmitterAgentTelemetryAdapter(emitter, correlation);

            if (_runContext?.Execution?.Telemetry is { } execTelemetry)
                return new ExecutionTelemetryAgentTelemetryAdapter(execTelemetry);

            return null;
        }
    }

    public IReadOnlyList<FailedToolAttempt> FailedToolAttempts => [.. _state.FailedToolState.FailedAttemptsBySignature.Values];

    public IReadOnlyDictionary<string, ArgumentDefinition> GetRuntimeArguments() => _config.RuntimeArguments;

    // ── Private helpers ───────────────────────────────────────────────────────

    private string SanitizeObservation(string observation)
    {
        if (!_capabilities.SanitizesObservations)
            return observation;
        return ObservationSanitizer.Sanitize(observation);
    }

    private bool ShouldBlockDuplicateExactToolCall(IReadOnlyList<AgentToolExecution> currentBatch, LlmToolCall toolCall, bool scopedDeduplication)
    {
        // When scopedDeduplication=true (P&E hermetic work tasks), only check the current
        // batch — parallel tasks are allowed to call the same tool with identical arguments.
        // When false (ReAct global context), also check the cross-turn executed-tool snapshot.
        var scope = scopedDeduplication ? currentBatch : (IEnumerable<AgentToolExecution>)_state.ExecutedToolCalls.Snapshot().Concat(currentBatch);

        return scope.Any(e => string.Equals(e.ToolName, toolCall.Name, StringComparison.Ordinal) && string.Equals(e.ArgumentsJson, toolCall.ArgumentsJson, StringComparison.Ordinal) && !e.IsError && !e.ReusedFromToolState);
    }

    private async ValueTask<AgentToolExecution?> TryBlockSemanticDuplicateHumanLoopAsync(LlmToolCall toolCall, PendingHumanReview pendingReview, IReadOnlyList<HumanLoopClarificationState> clarifications, CancellationToken cancellationToken)
    {
        var activeClarifications = clarifications.Where(c => c.IsActive).ToList();
        if (activeClarifications.Count == 0)
            return null;

        var candidateQuestions =
            pendingReview.QuestionsToAsk.Count > 0 ? pendingReview.QuestionsToAsk
            : !string.IsNullOrWhiteSpace(pendingReview.Question) ? [pendingReview.Question]
            : (IReadOnlyList<string>)[];

        if (candidateQuestions.Count == 0)
            return null;

        var prompt = HumanLoopHelper.BuildHumanLoopDuplicateReviewPrompt(candidateQuestions, activeClarifications);
        var duplicateCheckMessages = new List<LlmChatMessage> { new("user", prompt) };
        var duplicateCheckOptions = new LlmConversationOptions(ResponseSchema: AgentSchemas.HumanLoopDuplicateReview);

        LlmConversationResponse duplicateResponse;
        try
        {
            duplicateResponse = await InvokeLlmAsync(duplicateCheckMessages, null, duplicateCheckOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(duplicateResponse.Content))
            return null;

        HumanLoopDuplicateReviewAssessment? assessment;
        try
        {
            assessment = JsonSerializer.Deserialize<HumanLoopDuplicateReviewAssessment>(duplicateResponse.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return null;
        }

        if (assessment is not { ShouldBlock: true })
            return null;

        var blockedReason = string.IsNullOrWhiteSpace(assessment.ReuseGuidance) ? assessment.Reason : assessment.ReuseGuidance;
        return HumanLoopHelper.BuildBlockedHumanLoopToolExecution(toolCall, pendingReview, blockedReason);
    }

    /// <summary>
    /// STORY-0422.4 — Normalizes the raw tool execution result:
    /// <list type="bullet">
    /// <item>Empty/null content → structured "no data" observation</item>
    /// <item>Tool-level business error (IsError=true) → observation with actionable message</item>
    /// </list>
    /// The LLM always receives a meaningful message rather than silence or a raw error flag.
    /// </summary>
    private static AgentToolExecution NormalizeToolExecution(AgentToolExecution execution, string toolName)
    {
        if (string.IsNullOrWhiteSpace(execution.ResultContent))
        {
            var noDataMsg = $"Tool '{toolName}' returned no data for this request. The information may not be available.";
            return execution with { ResultContent = noDataMsg, IsError = true, FailedReason = noDataMsg };
        }

        if (execution.IsError && !string.IsNullOrWhiteSpace(execution.ResultContent))
        {
            // Business error — report the failure as-is. Do NOT invite the LLM to retry
            // or replan; unrecoverable tool errors should surface and terminate the agent.
            var businessErrorMsg = $"Tool '{toolName}' failed with an error: {execution.ResultContent}. This tool call cannot be completed.";
            return execution with { ResultContent = businessErrorMsg, FailedReason = businessErrorMsg };
        }

        return execution;
    }

    private async ValueTask<string> TryRefineResponseQualityAsync(string candidateResponse, int iteration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidateResponse))
            return candidateResponse;

        var threshold = _config.ResponseQualityThreshold;
        var assessmentPrompt = BuildQualityAssessmentPrompt(candidateResponse, threshold);
        assessmentPrompt = await ApplyGuardrailsAsync(AgentGuardrailContext.ResponseQualityAssessmentPrompt, assessmentPrompt, cancellationToken).ConfigureAwait(false);

        var assessmentResponse = await InvokeLlmAsync(
                [new LlmChatMessage("user", assessmentPrompt)],
                tools: null,
                new LlmConversationOptions(Temperature: _config.Temperature, MaxTokens: _config.MaxTokens, ResponseSchema: AgentSchemas.ResponseQualityAssessment, ToolChoiceMode: LlmToolChoiceMode.None, AllowParallelToolCalls: false),
                cancellationToken
            )
            .ConfigureAwait(false);

        var assessment = ParseQualityAssessment(assessmentResponse.Content);
        if (assessment.FinalScore >= threshold || string.IsNullOrWhiteSpace(assessment.RefinementGuideline))
            return candidateResponse;

        var refinementPrompt = $"{AgentPrompting.ResponseQualityRefinementPromptPrefix}\n" + $"- Final score: {assessment.FinalScore:0.0}/{threshold:0.0}\n" + $"- Refinement guideline: {assessment.RefinementGuideline}";
        _state.Messages.Add(new LlmChatMessage("user", refinementPrompt));
        _state.Messages.Add(new LlmChatMessage("user", AgentPrompting.BuildConversationalResponseFinalizationPrompt(_config.Prompt)));

        var refinedResponse = await InvokeLlmAsync(_state.Messages, tools: null, new LlmConversationOptions(Temperature: _config.Temperature, MaxTokens: _config.MaxTokens, ToolChoiceMode: LlmToolChoiceMode.None, AllowParallelToolCalls: false), cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(refinedResponse.Content) ? candidateResponse : refinedResponse.Content.Trim();
    }


    private sealed record QualityAssessmentSnapshot(double FinalScore, string RefinementGuideline);

    private static QualityAssessmentSnapshot ParseQualityAssessment(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Quality assessment returned an empty payload.");

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            return new QualityAssessmentSnapshot(root.GetProperty("final_score").GetDouble(), root.TryGetProperty("refinement_guideline", out var rg) ? rg.GetString() ?? string.Empty : string.Empty);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Quality assessment returned invalid JSON.", ex);
        }
    }

    private static string BuildQualityAssessmentPrompt(string candidate, double threshold) =>
        $"""
            <response_quality_assessment_request>
            <draft_response><![CDATA[{candidate}]]></draft_response>
            <target_threshold>{threshold:0.0}</target_threshold>
            </response_quality_assessment_request>
            """;

    // ── Port adapters ─────────────────────────────────────────────────────────

    /// <summary>
    /// Adapts <see cref="IExecutionTelemetry"/> (chain-level port) to <see cref="IAgentTelemetry"/>
    /// (runtime surface). Correlation context is supplied via <see cref="AgentRunServices"/>.
    /// </summary>
    /// <summary>
    /// Adapts the run's <see cref="IAgentTelemetryEmitter"/> — the one the agent's start and its model calls are emitted
    /// with — to <see cref="IAgentTelemetry"/>, each event correlated to its step.
    /// </summary>
    private sealed class EmitterAgentTelemetryAdapter(IAgentTelemetryEmitter emitter, ExecutionCorrelationContext correlation) : IAgentTelemetry
    {
        public ValueTask EmitAsync<TEvent>(TEvent payload, string stepId, CancellationToken cancellationToken = default)
            where TEvent : class, IAgentTelemetryEvent => emitter.EmitAsync(correlation with { StepId = stepId }, payload, cancellationToken);
    }

    private sealed class ExecutionTelemetryAgentTelemetryAdapter(IExecutionTelemetry telemetry) : IAgentTelemetry
    {
        public ValueTask EmitAsync<TEvent>(TEvent payload, string stepId, CancellationToken cancellationToken = default)
            where TEvent : class, IAgentTelemetryEvent => telemetry.EmitAsync(eventName: typeof(TEvent).Name, payload: payload, stepId: stepId, ct: cancellationToken);
    }
}
