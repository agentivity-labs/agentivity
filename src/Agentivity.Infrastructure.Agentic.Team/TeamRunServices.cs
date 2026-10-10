using Agentivity.Infrastructure.Core.Diagnostics;
using Agentivity.Infrastructure.Core.Execution;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Observability.Contracts;
using Agentivity.Observability.Contracts.Correlation;

namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// Runtime services for a single team execution.
/// Implements <see cref="IRunService"/> — the universal runtime services contract —
/// and adds team-specific context: the propagated <see cref="RunContext"/>.
/// </summary>
public sealed class TeamRunServices : IRunService
{
    // ── IRunService ───────────────────────────────────────────────────────────

    public Guid RunId { get; init; }
    public ExecutionCorrelationContext? CorrelationContext { get; init; }

    public Func<CancellationToken, ValueTask>? Checkpoint { get; init; }
    public Action<string>? PublishStatus { get; init; }
    public Action<Action<DebugTraceAccumulator>>? UpdateTrace { get; init; }
    public Action<string>? LogDebug { get; init; }

    ValueTask IRunService.CheckpointAsync(CancellationToken ct) => Checkpoint?.Invoke(ct) ?? ValueTask.CompletedTask;

    void IRunService.PublishStatus(string message) => PublishStatus?.Invoke(message);

    void IRunService.UpdateTrace(Action<DebugTraceAccumulator> updater) => UpdateTrace?.Invoke(updater);

    void IRunService.LogDebug(string message) => LogDebug?.Invoke(message);

    // ── Team-specific ─────────────────────────────────────────────────────────

    /// <summary>
    /// The propagated run context — carries RunId, ExecutionId, HIL flag, and
    /// the shared <see cref="Agentivity.Observability.Contracts.ExecutionContext"/>
    /// (telemetry, trace, HIL channel) from the parent execution chain.
    /// </summary>
    public RunContext? RunContext { get; init; }
}
