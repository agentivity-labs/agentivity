using Agentivity.Infrastructure.Execution.Runs;

namespace Agentivity.Infrastructure.Interactions;

/// <summary>
/// Per-engine re-entry provider for the unified resume path.
/// <para>
/// <see cref="DurableRunService"/> owns everything generic — locating the run, walking the
/// persisted run tree to the branch root, gating on the lifecycle state, rehydrating the
/// <see cref="RunContext"/> (same ExecutionId/StreamId via the execution registry). The engine
/// only knows how to re-enter its own kind of run from its own progress store.
/// </para>
/// </summary>
public interface IResumableEngine
{
    /// <summary>Run kind this engine owns: "workflow", "team" or "agent" (matches RunRecord.EntityKind).</summary>
    string Kind { get; }

    /// <summary>
    /// Re-enters the suspended run. <paramref name="run"/> is the run's one live, cache-registered
    /// <see cref="RunContext"/> — the same instance <see cref="DurableRunService"/> found via the
    /// repository and already prepared with <see cref="RunContext.PrepareForResume"/>; engines must
    /// mutate this instance in place, never build a second one for the same run (a copy silently
    /// diverges from what every other reader of the repository's cache sees). For a HIL resume the
    /// interaction response has already been saved to the store (cascades read it by requestId);
    /// for a crash recovery (<paramref name="response"/> is null) the run re-enters from its last
    /// checkpoint. Long-running continuation should be fire-and-forget — the caller only awaits the
    /// re-entry hand-off.
    /// </summary>
    ValueTask ReenterAsync(RunContext run, InteractionResponse? response, CancellationToken ct);
}
