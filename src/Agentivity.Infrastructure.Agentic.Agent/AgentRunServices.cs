using System.Text.Json.Nodes;
using Agentivity.Infrastructure.Agentic.Agent.Guardrails;
using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Execution;
using Agentivity.Infrastructure.Core.Llm;
using Agentivity.Observability.Abstractions;
using Agentivity.Observability.Contracts.Correlation;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Runtime services for a single agent execution.
/// Implements <see cref="IRunService"/> — the universal runtime services contract —
/// and adds agent-specific capabilities: LLM invocation, tool execution, HIL suspension.
/// <para>
/// Replaces <see cref="AgentRunPorts"/>. All delegates are fire-and-forget-safe
/// unless otherwise noted.
/// </para>
/// </summary>
public sealed class AgentRunServices : IAgentRunServices
{
    public static readonly AgentRunServices Empty = new();

    // ── IRunService ───────────────────────────────────────────────────────────

    public Guid RunId { get; init; }
    public ExecutionCorrelationContext? CorrelationContext { get; init; }

    /// <summary>Where the telemetry the agent's runtime emits goes (a replan, say), with <see cref="CorrelationContext"/>.</summary>
    public IAgentTelemetryEmitter? TelemetryEmitter { get; init; }

    // ── Common delegates (init-settable; IRunService methods delegate to them) ─

    public Func<CancellationToken, ValueTask>? Checkpoint { get; init; }
    public Action<string>? PublishStatus { get; init; }
    public Action<Action<DebugTraceAccumulator>>? UpdateTrace { get; init; }
    public Action<string>? LogDebug { get; init; }

    // ── IRunService explicit implementations ──────────────────────────────────

    ValueTask IRunService.CheckpointAsync(CancellationToken cancellationToken) => Checkpoint?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;

    void IRunService.PublishStatus(string message) => PublishStatus?.Invoke(message);

    void IRunService.UpdateTrace(Action<DebugTraceAccumulator> updater) => UpdateTrace?.Invoke(updater);

    void IRunService.LogDebug(string message) => LogDebug?.Invoke(message);

    // ── Scratchpad

    public Action<string, object?>? SetState { get; init; }
    public Func<string, (bool Found, object? Value)>? GetState { get; init; }

    // ── Agent-specific ────────────────────────────────────────────────────────

    /// <summary>Invokes the LLM for this run. Required.</summary>
    public Func<IReadOnlyList<LlmChatMessage>, IReadOnlyList<JsonObject>?, LlmConversationOptions, CancellationToken, ValueTask<LlmConversationResponse>>? InvokeLlm { get; init; }

    /// <summary>Agent execution runtime port (LLM + tool execution). Null in test stubs.</summary>
    public IAgentLlmProxy? RuntimePort { get; init; }

    /// <summary>Human-in-the-loop proxy. Null when HIL is disabled.</summary>
    public IAgentHilProxy? HilProxy { get; init; }

    /// <summary>
    /// Shared, run-scoped accumulator for non-blocking (Warn) guardrail matches — the SAME list
    /// instance is referenced from every guardrail-evaluation call site for this run (early
    /// prompt-layer checks in <see cref="AgentHost.RunAsync"/>, every
    /// <see cref="IAgentManifest.ApplyGuardrailsAsync"/> call downstream), so it converges into
    /// one list with no merging needed. Read back into <see cref="AgentRunResult.GuardrailWarnings"/>
    /// at the end of the run. Null in contexts that don't care about surfacing warnings (most
    /// test stubs).
    /// </summary>
    public List<AgentGuardrailMatch>? GuardrailWarningSink { get; init; }
}
