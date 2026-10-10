namespace Agentivity.Infrastructure.Execution.Runs;

/// <summary>
/// Minimal, mutable holder for a rolling prose summary of "everything relevant that happened in
/// this scope so far" — deliberately just state, no compaction logic (that stays in
/// <c>AgentSessionCompaction</c>, shared by every consumer).
/// <para>
/// A <see cref="RunContext"/> owns one of these explicitly (<see cref="RunContext.OwnTranscriptScope"/>)
/// only when something at creation time decided this run should be a scope boundary — a team run
/// (its members share this one instance directly instead of each keeping a private summary), or a
/// standalone/workflow agent run (its own private scope, replacing the old per-session
/// <c>SummaryStep</c> transcript). A run that doesn't own one participates in the nearest owning
/// ancestor's scope instead (see <see cref="RunContext.LastTranscriptSummary"/>) — ownership is
/// always decided once, explicitly, at the run's creation, never inferred from <c>RunKind</c> or
/// walked to the absolute root of the parent chain.
/// </para>
/// </summary>
public sealed class TranscriptScope
{
    public string? Summary { get; set; }
}
