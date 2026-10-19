using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Graph;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// The single result type returned by <see cref="IAgentManifest"/>-aware runtimes.
/// Replaces <see cref="AgenticModeFinalizationResult"/> at the runtime boundary.
/// </summary>
public sealed record AgentRunResult(string Response, string FinishReason, int ExecutedIterations)
{
    /// <summary>
    /// Execution journal produced by the runtime, when journal collection is enabled.
    /// Null for runtimes or license tiers that do not enable journal collection.
    /// </summary>
    public Graph.GraphJournal? Journal { get; init; }

    /// <summary>
    /// User-facing explanation when the agent completed in a degraded state.
    /// Non-null only when <see cref="FinishReason"/> is one of the degraded constants
    /// defined in <see cref="AgenticFinishReason"/> (e.g. <see cref="AgenticFinishReason.ToolNotAvailable"/>).
    /// </summary>
    public string? DegradedReason { get; init; }

    /// <summary>True when the agent completed in a degraded state with a user-facing explanation.</summary>
    public bool IsDegraded => DegradedReason is not null;

    /// <summary>
    /// Final conversation messages at end of run.
    /// Populated by <c>AgentHost.RunAsync</c>; consumed by workflow completion to persist the session.
    /// </summary>
    public IReadOnlyList<LlmChatMessage>? FinalMessages { get; init; }

    /// <summary>
    /// All tool executions recorded during the run.
    /// Populated by <c>AgentHost.RunAsync</c>; consumed by workflow completion and suspend-outputs.
    /// </summary>
    public IReadOnlyList<AgentToolExecution>? ExecutedToolCalls { get; init; }

    /// <summary>
    /// Total tokens consumed during the run (from <c>AgentTraceBudgetState.TotalTokensUsed</c>).
    /// Populated by <c>AgentHost.RunAsync</c>; consumed for telemetry on the workflow path.
    /// </summary>
    public int TotalTokensUsed { get; init; }

    /// <summary>
    /// Number of conversation messages persisted to the session by <see cref="IAgentSessionProxy.SaveAsync"/>.
    /// Populated by <c>AgentHost.RunAgentAsync</c>; 0 when the session is disabled or not persisted.
    /// </summary>
    public int PersistedMessageCount { get; init; }

    /// <summary>
    /// Every guardrail match recorded during the run with a non-blocking action (Warn — a
    /// Block match never reaches here, since <see cref="AgentHost.ApplyGuardrailsAsync"/> throws
    /// immediately instead of returning). Empty when no guardrail matched anything. Exists so a
    /// workflow author can branch on the OUTCOME of a guardrail (via an ordinary If/Switch node
    /// downstream of the agent) instead of the engine baking in a fixed "what happens on warn"
    /// behavior — see EPIC-0462 P1-8's follow-up discussion: a guardrail match is DATA the
    /// workflow decides what to do with (log it, retry, route to human review...), not a built-in
    /// action.
    /// </summary>
    public IReadOnlyList<AgentGuardrailMatch> GuardrailWarnings { get; init; } = [];

    /// <summary>
    /// Optional signal from the agent to its orchestrator about how execution should continue.
    /// The orchestrator (team, workflow, etc.) decides whether and how to act on it.
    /// Null when the agent produced a self-contained response with no orchestration needs.
    /// </summary>
    public string? OrchestrationHint { get; init; }

    /// <summary>
    /// The protocol-defined action the turn concluded with — see
    /// <see cref="Agentivity.Infrastructure.Agentic.Agent.AgentRunState.OrchestrationAction"/>. Null for a plain conclusion.
    /// </summary>
    public string? OrchestrationAction { get; init; }

    /// <summary>
    /// Protocol-defined side information — see
    /// <see cref="Agentivity.Infrastructure.Agentic.Agent.AgentRunState.OrchestrationNotes"/>. Best-effort
    /// information: empty when nothing was declared, never an error.
    /// </summary>
    public IReadOnlyList<string> OrchestrationNotes { get; init; } = [];

    /// <summary>Terminal execution status of the agent run.</summary>
    public ExecutableStatus Status { get; init; } = ExecutableStatus.Completed;

    /// <summary>
    /// Id of the pending interaction (e.g. HIL request) the agent is waiting for.
    /// Non-null when Status is Suspended and the agent needs an external response to continue.
    /// </summary>
    public string? WaitingForToken { get; init; }

    public static AgentRunResult MaxIterations(int maxIterations) => new(string.Empty, AgenticFinishReason.MaxIterations, maxIterations);

    /// <summary>
    /// Creates a degraded result: the agent could not complete normally but produces
    /// a user-readable explanation instead of throwing.
    /// </summary>
    /// <param name="finishReason">One of the degraded <see cref="AgenticFinishReason"/> constants.</param>
    /// <param name="userFacingMessage">A clear, actionable message for the end user.</param>
    public static AgentRunResult Degraded(string finishReason, string userFacingMessage) => new(userFacingMessage, finishReason, 0) { DegradedReason = userFacingMessage };
}

/// <summary>
/// Well-known keys for <see cref="IAgentManifest.GetPolicyRules"/>.
/// The runtime chooses which context it needs; the infrastructure maps to policy rules.
/// </summary>
public static class AgentPolicyContext
{
    public const string Execution = "execution";
    public const string HumanLoop = "human_loop";
    public const string Planning = "planning";
    public const string TaskBatch = "task_batch";
    public const string Join = "join";
    public const string Replanning = "replanning";
    public const string PlanRefinement = "plan_refinement";
    public const string Workspace = "workspace";
    public const string WorkspaceConsolidation = "workspace_consolidation";
}

/// <summary>
/// Well-known keys for <see cref="IAgentManifest.ApplyGuardrails"/>.
/// </summary>
public static class AgentGuardrailContext
{
    public const string ExecutionPrompt = "execution_prompt";
    public const string TaskBatchPrompt = "task_batch_prompt";
    public const string JoinPrompt = "join_prompt";
    public const string ReplanningPrompt = "replanning_prompt";
    public const string FinalResponse = "final_response";
    public const string HumanLoopPrompt = "human_loop_prompt";
    public const string ResponseQualityAssessmentPrompt = "response_quality_assessment_prompt";
}

/// <summary>
/// Single coherent platform service object passed to
/// <see cref="IAgenticModeRuntime.RunAsync(IAgentManifest, CancellationToken)"/>.
///
/// Provides all capabilities a runtime needs: conversation state, LLM invocation,
/// tool execution, policy rules, guardrails, persistence, human-loop, and tracing.
/// The runtime owns its full execution loop — the infrastructure is a service locator,
/// not a coordinator.
/// </summary>
public interface IAgentManifest
{
    // ── Suspension state ──────────────────────────────────────────────────────

    /// <summary>
    /// True once a HIL tool call (e.g. <c>ask_human</c>) has signalled suspension.
    /// Mode runtimes should check this after <see cref="ExecuteToolsAsync"/> and stop
    /// the iteration loop to avoid making unnecessary extra LLM calls.
    /// </summary>
    /// <summary>
    /// True while the agent loop should keep making LLM calls.
    /// False once the agent has signalled that its turn is over — either because it is
    /// waiting for an external response (interaction pending) or because it emitted an
    /// orchestration hint (routing to another agent or entity).
    /// Mode runtimes check this after each tool batch and break when false.
    /// </summary>
    bool IsRunning { get; }

    // ── Conversation ──────────────────────────────────────────────────────────

    /// <summary>The original user prompt for this execution.</summary>
    string Prompt { get; }

    /// <summary>Live conversation messages. Mutations are reflected immediately.</summary>
    IReadOnlyList<LlmChatMessage> Messages { get; }

    /// <summary>
    /// The first system message configured for this agent (persona, guardrails).
    /// Null when no system message is present.
    /// Runtimes that build hermetic, role-scoped message lists should use this instead
    /// of cloning <see cref="Messages"/> to avoid inheriting unrelated conversation history.
    /// </summary>
    LlmChatMessage? SystemMessage { get; }

    void AddMessage(LlmChatMessage message);

    /// <summary>
    /// Inserts a system prompt if not already present.
    /// Respects existing system prompt ordering conventions.
    /// </summary>
    void EnsureSystemPrompt(string systemPrompt);

    // ── LLM ───────────────────────────────────────────────────────────────────

    double Temperature { get; }
    int? MaxTokens { get; }
    int? MaxIterations { get; }
    LlmToolChoiceMode ToolChoiceMode { get; }
    bool AllowParallelToolCalls { get; }
    string ResponseSchema { get; }
    bool RequiresStructuredResponseFinalization { get; }

    /// <summary>
    /// Schema to request for a plain-text (no tool call) reply this turn: the agent's own
    /// <see cref="ResponseSchema"/> when set, otherwise the human-loop reply schema (folds in
    /// <c>needs_human_input</c>/<c>is_selection_question</c>) when human loop is enabled, otherwise null.
    /// </summary>
    string? ReplySchema { get; }

    ValueTask<LlmConversationResponse> InvokeLlmAsync(IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<JsonObject>? tools, LlmConversationOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// The agent's own per-iteration turn (<see cref="Steps.Implementations.MainTurnStep"/>) —
    /// mode runtimes call this instead of building <see cref="LlmConversationOptions"/> and
    /// calling <see cref="InvokeLlmAsync"/> directly, so budget/schema resolution lives in one
    /// place.
    /// </summary>
    ValueTask<LlmConversationResponse> RunMainTurnAsync(IReadOnlyList<JsonObject>? tools, bool allowParallelToolCalls, CancellationToken cancellationToken);

    /// <summary>
    /// Rolls the unsummarized tail of the conversation into
    /// <see cref="Agentivity.Infrastructure.Execution.Runs.RunContext.LastTranscriptSummary"/>
    /// if it's grown past the threshold (<see cref="Steps.Implementations.SummaryStep"/>) — a
    /// no-op otherwise. Mode runtimes call this once per iteration, before
    /// <see cref="RunMainTurnAsync"/>, so every call after it benefits from the bounded window.
    /// See <c>.claude/EPIC-agent-transcript-summary.md</c>.
    /// </summary>
    ValueTask RunSummaryIfDueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Given a plain-text (no tool call) LLM response, forces a single constrained follow-up call
    /// restricted to the turn-conclusion tool so the agent states its outcome explicitly (done,
    /// needs_input, or — team members only — a real handoff target) instead of trailing off.
    /// Tool-call responses pass through unchanged.
    /// </summary>
    ValueTask<LlmConversationResponse> ResolveTurnConclusionAsync(LlmConversationResponse response, int iteration, CancellationToken cancellationToken);

    /// <summary>
    /// Narrower HIL-only path for a dedicated standalone call the caller already built with
    /// <see cref="AgentSchemas.ConversationalReply"/> and no tools (e.g. a synthesis or work-task
    /// qualification call) — skips the auto-detection since the caller already knows which schema
    /// it requested. Unrelated to <see cref="ResolveTurnConclusionAsync"/>.
    /// </summary>
    ValueTask<LlmConversationResponse> ResolveMergedHumanLoopReplyAsync(LlmConversationResponse response, int iteration, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this agent may stop and wait for a free-text reply from a person — the case where
    /// <see cref="ResolveMergedHumanLoopReplyAsync"/> does something. Without it, a call made only to feed that step is not made.
    /// </summary>
    bool SupportsFreeTextHumanLoop => true;

    // ── Tools ─────────────────────────────────────────────────────────────────

    IReadOnlyList<JsonObject> ToolDefinitions { get; }

    /// <summary>
    /// Executes tool calls with full platform semantics: deduplication, human-loop
    /// tool dispatch, observation sanitization, and tracing. Results are appended
    /// to <see cref="Messages"/> as tool role messages automatically.
    /// </summary>
    ValueTask<IReadOnlyList<AgentToolExecution>> ExecuteToolsAsync(IReadOnlyList<LlmToolCall> toolCalls, int iteration, CancellationToken cancellationToken);

    /// <summary>
    /// Executes tool calls with full platform semantics. Results are appended to
    /// <paramref name="targetMessages"/> instead of the shared <see cref="Messages"/> buffer,
    /// allowing work tasks to maintain an isolated local context.
    /// <see cref="ExecutedToolCalls"/> is still updated for output tracking.
    /// </summary>
    ValueTask<IReadOnlyList<AgentToolExecution>> ExecuteToolsAsync(IReadOnlyList<LlmToolCall> toolCalls, int iteration, List<LlmChatMessage> targetMessages, CancellationToken cancellationToken);

    /// <summary>All tool calls executed so far in this run.</summary>
    IReadOnlyList<AgentToolExecution> ExecutedToolCalls { get; }

    /// <summary>
    /// Delivers plain assistant-authored text to the human-loop channel immediately, the same way
    /// <c>interact_with_human(mode: "send")</c> does — but for a response's own <c>content</c> field,
    /// with no tool call involved. Call this whenever <c>content</c> accompanies a tool call: today
    /// that text is otherwise silently discarded (see <see cref="FinalizeAsync"/>), which is what
    /// makes a "deliver this message, then decide what's next" turn indistinguishable from one that
    /// is still doing real work — the turn-completion signal ("no tool call") only fires once the
    /// model stops calling tools entirely, which a fire-and-forget human-loop message never needs to
    /// do to be fully delivered.
    /// <para>
    /// No-op when human loop is disabled, no channel is configured, or <paramref name="concurrentToolCalls"/>
    /// includes a human-loop or routing tool call — those already have their own dedicated delivery
    /// channel (the tool call's own content/message), so <c>content</c> alongside them is redundant
    /// scratch text, not a second message to show.
    /// </para>
    /// </summary>
    ValueTask DispatchLiveContentAsync(string content, IReadOnlyList<LlmToolCall> concurrentToolCalls, int iteration, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    // ── Policies ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the resolved policy rules for the given context.
    /// Use <see cref="AgentPolicyContext"/> constants as keys.
    /// Returns an empty list when no rules apply.
    /// </summary>
    IReadOnlyList<string> GetPolicyRules(string context);

    // ── Guardrails ────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies guardrails for the given stage to a prompt or content string.
    /// Use <see cref="AgentGuardrailContext"/> constants as stage keys.
    /// Returns the original content if no guardrail applies.
    /// </summary>
    ValueTask<string> ApplyGuardrailsAsync(string stage, string content, CancellationToken cancellationToken = default);

    // ── Platform services ─────────────────────────────────────────────────────

    ValueTask PersistAgentStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes a typed scratchpad data object into the checkpoint store so it survives
    /// suspension and server restarts. Call this before <see cref="PersistAgentStateAsync"/>.
    /// </summary>
    void SetScratchpad<T>(string key, T data)
        where T : class;

    /// <summary>
    /// Reads a typed scratchpad data object that was previously written via <see cref="SetScratchpad{T}"/>.
    /// Returns <see langword="false"/> when the key is absent or cannot be deserialized.
    /// </summary>
    bool TryRestoreScratchpad<T>(string key, out T? data)
        where T : class;

    /// <summary>
    /// Terminal call. Applies final guardrails and optional quality refinement,
    /// then returns the authoritative <see cref="AgentRunResult"/>.
    /// Must be called exactly once per successful execution path.
    /// </summary>
    ValueTask<AgentRunResult> FinalizeAsync(string response, string finishReason, int executedIterations, CancellationToken cancellationToken);

    /// <summary>
    /// Single decision point for what happens after an already-resolved LLM response (i.e. one that
    /// already went through <see cref="ResolveHumanLoopReplyAsync"/>): records the assistant message,
    /// and — a real tool call is the only thing that keeps a mode runtime's per-turn loop going, per
    /// its ReAct contract — either executes those tool calls and returns <see langword="null"/> to
    /// signal the loop should iterate again (the run is still active), or calls
    /// <see cref="FinalizeAsync"/> and returns its result: unconditionally when there is no tool call
    /// at all (the turn's answer, full stop — nothing here re-classifies it), or when tool execution
    /// itself ended the run (suspension, handoff). Shared by every mode runtime's per-turn loop so
    /// this branch is implemented once instead of copied per runtime.
    /// </summary>
    ValueTask<AgentRunResult?> TryFinalizeTurnAsync(LlmConversationResponse response, int iteration, CancellationToken cancellationToken);


    // ── Resume state ──────────────────────────────────────────────────────────

    /// <summary>
    /// State from a previously suspended (human-review) execution.
    /// Contains pending tool calls, iterations completed, and finish reason.
    /// </summary>
    HumanReviewResumeState ResumeState { get; }

    // ── Tracing ───────────────────────────────────────────────────────────────

    void AppendTrace(TraceEvent traceEvent);

    void SetLatestResponse(string content);

    // ── Extended platform services ────────────────────────────────────────────

    /// <summary>
    /// Publishes a status message visible in the workflow UI (graph node display).
    /// Also emits an ILogger Information event for internal diagnostics.
    /// Use this for messages the operator always cares about.
    /// </summary>
    void PublishStatusMessage(string message);

    /// <summary>
    /// Emits an internal diagnostic message at <c>Debug</c> level via ILogger (Serilog → Console / File / OTLP).
    /// Not forwarded to the UI. Visible only when <c>Serilog:MinimumLevel:Override:Agentivity</c>
    /// is set to <c>Debug</c> (or lower) in appsettings.json. Use this for prompt sizes,
    /// token counts, timings, and other low-level runtime details that are noise in production.
    /// Default no-op implementation keeps existing fakes and stubs unaffected.
    /// </summary>
    void LogDebug(string message) { }

    /// <summary>
    /// Structured telemetry surface for the current run.
    /// Null when no telemetry sink is configured.
    /// </summary>
    IAgentTelemetry? Telemetry { get; }

    /// <summary>
    /// Failed tool attempts from this run. Used by planning actors for replanning context.
    /// </summary>
    IReadOnlyList<FailedToolAttempt> FailedToolAttempts { get; }
}
