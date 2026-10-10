namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// The result of a completed (or failed) team run, returned by <see cref="ITeamEngine.ExecuteAsync"/>.
/// </summary>
public sealed record AgenticTeamResult(string RunId, TeamRunState FinalState, IReadOnlyList<TaskOutput> Outputs)
{
    public bool IsSuccess => FinalState == TeamRunState.Completed;

    /// <summary>Structured error when <see cref="FinalState"/> is <see cref="TeamRunState.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// For manager-led orchestration: the manager agent's final synthesis text.
    /// Null for other orchestrator patterns.
    /// </summary>
    public string? FinalSynthesis { get; init; }

    /// <summary>
    /// Everything the team's members declared they could not cover (see
    /// <see cref="TaskOutput.NotCovered"/>), across the whole run, without duplicates. Computed from
    /// the outputs, so it holds for every orchestrator without any of them doing anything.
    /// Best-effort information: empty when nothing was declared, never an error.
    /// </summary>
    public IReadOnlyList<string> NotCovered => Outputs.SelectMany(o => o.NotCovered).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Opaque resume token from the suspended executable, propagated upward through the
    /// execution hierarchy. Null when <see cref="FinalState"/> is not <see cref="TeamRunState.Paused"/>.
    /// </summary>
    public string? WaitingForToken { get; init; }
}
