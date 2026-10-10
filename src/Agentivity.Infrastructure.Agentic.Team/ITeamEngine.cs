using Agentivity.Domain.Team;
using Agentivity.Infrastructure.Core.Execution;
using Agentivity.Infrastructure.Team;

namespace Agentivity.Infrastructure.Agentic.Team;

/// <summary>
/// Top-level multi-agent engine contract. Implements <see cref="IExecutionUnit{TConfig, TResult}"/>
/// for uniform alignment with workflow nodes and agent mode runtimes.
/// <para>
/// <see cref="IExecutionUnit{TConfig,TResult}.ExecuteAsync"/> is the primary entry point.
/// <see cref="ExecuteAsync(TeamRunRequest, CancellationToken)"/> is kept as a convenience overload
/// for callers that already hold a <see cref="TeamRunRequest"/> (API layer, legacy adapters).
/// </para>
/// </summary>
public interface ITeamEngine : IExecutionUnit<TeamConfig, AgenticTeamResult>
{
    /// <summary>
    /// Executes a team run from a <see cref="TeamConfig"/> and <see cref="TeamRunServices"/>.
    /// This is the canonical aligned entry point.
    /// </summary>
    ValueTask<AgenticTeamResult> ExecuteAsync(TeamConfig config, TeamRunServices services, CancellationToken cancellationToken = default);

    // ── IExecutionUnit bridge ─────────────────────────────────────────────────
    ValueTask<AgenticTeamResult> IExecutionUnit<TeamConfig, AgenticTeamResult>.ExecuteAsync(TeamConfig config, IRunService services, CancellationToken cancellationToken) =>
        services is TeamRunServices ts ? ExecuteAsync(config, ts, cancellationToken) : throw new InvalidOperationException($"{nameof(ITeamEngine)} requires {nameof(TeamRunServices)}.");

    /// <summary>
    /// Convenience overload — builds <see cref="TeamConfig"/> + <see cref="TeamRunServices"/>
    /// from the legacy <see cref="TeamRunRequest"/> DTO and delegates to the aligned entry point.
    /// </summary>
    ValueTask<AgenticTeamResult> ExecuteAsync(TeamRunRequest run, CancellationToken cancellationToken = default);

    ValueTask PauseAsync(string runId, CancellationToken cancellationToken = default);
    ValueTask ResumeAsync(string runId, CancellationToken cancellationToken = default);
    ValueTask CancelAsync(string runId, CancellationToken cancellationToken = default);
    ValueTask<OrchestratorValidationResult> ValidateAsync(TeamEntity team, CancellationToken cancellationToken = default);
}
