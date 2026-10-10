using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Observability.Contracts.Correlation;

namespace Agentivity.Infrastructure.Core.Execution;

/// <summary>
/// Common runtime services contract shared by all execution engines:
/// workflow nodes (<c>WorkflowRunServices</c>), agent mode runtimes (<c>AgentRunServices</c>),
/// and team engines (<c>TeamRunServices</c>).
/// <para>
/// Carries only platform-level delegates — no config data, no domain state.
/// Engine-specific capabilities are added by concrete implementations.
/// </para>
/// </summary>
public interface IRunService
{
    /// <summary>Unique identifier of the current run.</summary>
    Guid RunId { get; }

    /// <summary>
    /// Persists current execution state (checkpoint for pause/resume/cancel).
    /// No-op when not running under a managed engine.
    /// </summary>
    ValueTask CheckpointAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publishes a status message visible to the operator (UI node display, SSE stream).
    /// Fire-and-forget — never throws.
    /// </summary>
    void PublishStatus(string message);

    /// <summary>
    /// Appends a structured trace event to the debug trace accumulator.
    /// Fire-and-forget — never throws.
    /// </summary>
    void UpdateTrace(Action<DebugTraceAccumulator> updater);

    /// <summary>
    /// Emits an internal diagnostic message at Debug level.
    /// Not forwarded to the UI — only visible in structured logs when debug threshold is met.
    /// </summary>
    void LogDebug(string message);

    /// <summary>OTel correlation context for the current execution unit.</summary>
    ExecutionCorrelationContext? CorrelationContext { get; }
}
