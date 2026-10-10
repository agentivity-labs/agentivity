using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Agentic.Agent.Policies;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Threading;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Mutable runtime state for a single agent execution.
/// Initialised before the run starts; mutated freely by the mode runtime during the loop.
/// </summary>
public sealed class AgentRunState
{
    public AgentRunState(SessionContext sessionContext, List<LlmChatMessage> messages, AgentTraceMetadata traceMetadata, List<AgentToolExecution> executedToolCalls, FailedToolTrackingState failedToolState)
    {
        SessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        TraceMetadata = traceMetadata ?? throw new ArgumentNullException(nameof(traceMetadata));
        ExecutedToolCalls = new ThreadSafeAppendLog<AgentToolExecution>(executedToolCalls ?? throw new ArgumentNullException(nameof(executedToolCalls)));
        FailedToolState = failedToolState ?? throw new ArgumentNullException(nameof(failedToolState));
    }

    public SessionContext SessionContext { get; }
    public List<LlmChatMessage> Messages { get; }
    public AgentTraceMetadata TraceMetadata { get; }
    public ThreadSafeAppendLog<AgentToolExecution> ExecutedToolCalls { get; }
    public FailedToolTrackingState FailedToolState { get; }
    public HumanReviewResumeState ResumeState { get; set; } = HumanReviewResumeState.None;

    /// <summary>
    /// True while the agent loop should continue making LLM calls.
    /// Set to false by any signal that ends the agent's turn:
    /// - a pending interaction (HIL, external event)
    /// - an orchestration hint (routing to another agent)
    /// - any other terminal tool call
    /// Mode runtimes check this after each tool batch: if not running, break the loop.
    /// </summary>
    public bool IsRunning { get; set; } = true;

    /// <summary>
    /// Optional signal from the agent to its orchestrator about how execution should continue.
    /// The orchestrator (team, workflow, etc.) decides whether and how to act on it.
    /// Null when the agent produced a self-contained response with no orchestration needs.
    /// </summary>
    public string? OrchestrationHint { get; set; }

    /// <summary>
    /// The protocol-defined action the turn concluded with (opaque to the engine — set by the
    /// <see cref="ITurnConclusionTool"/> in use), handed back to the orchestrator unchanged. Null for a
    /// plain conclusion.
    /// </summary>
    public string? OrchestrationAction { get; set; }

    /// <summary>
    /// True once this run has delivered content live — a <c>send</c>-mode human-loop call, or plain
    /// text dispatched by <see cref="Agentivity.Infrastructure.Agentic.Agent.AgentManifest.DispatchLiveContentAsync"/>
    /// — through its own dedicated channel, rather than through the run's own final response text.
    /// Read by <c>AgentHost</c> to tell "this run legitimately has nothing left to say — it already
    /// said it, live" apart from "this run produced nothing at all", which stays an error. Sticky for
    /// the whole run, not just the current turn: a later turn that concludes with an empty response
    /// after an earlier turn's send is exactly the same legitimate case.
    /// </summary>
    public bool DeliveredLiveContent { get; set; }

    /// <summary>
    /// Protocol-defined side information collected from the turn-conclusion calls over this run. Best
    /// effort only — never blocks anything. Surfaced as <see cref="AgentRunResult.OrchestrationNotes"/>.
    /// </summary>
    public List<string> OrchestrationNotes { get; } = [];

    /// <summary>Terminal execution status propagated to <see cref="AgentRunResult.Status"/>.</summary>
    public ExecutableStatus Status { get; set; } = ExecutableStatus.Completed;

    /// <summary>
    /// Optional user-facing message set by the agent when calling <c>agent_next_step</c>.
    /// Used as the agent's response text when the LLM produced no text content in the same turn.
    /// </summary>
    public string? HandoffMessage { get; set; }

    /// <summary>
    /// The main turn's own free-text response, held here for the brief window between the main
    /// turn ending and the decision step (<c>agent_next_step</c>) that follows it resolving —
    /// see <see cref="IAgentManifest.ResolveTurnConclusionAsync"/>. The decision step's schema has
    /// no text field of its own (deliberately — nowhere for a model to badly restate its answer),
    /// so this is the only source ever used for what's delivered to the user.
    /// </summary>
    public string? PendingTurnDraft { get; set; }

    /// <summary>
    /// Opaque token identifying the pending interaction (e.g. HIL request id).
    /// Non-null when the agent is waiting for an external response before it can continue.
    /// </summary>
    public string? WaitingForToken { get; set; }

}
