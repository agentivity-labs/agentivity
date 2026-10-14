using Agentivity.Domain.Executable;
using Agentivity.Domain.Execution;
using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Agentic.Team;
using Agentivity.Infrastructure.Execution.Executable;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Registry;
using Agentivity.Infrastructure.Repository;
using Agentivity.Observability.Contracts;

namespace Agentivity.Infrastructure.Fabric;

/// <summary>
/// Adapts <see cref="ITeamEngine"/> towards <see cref="IExecutable"/>.
/// </summary>
public sealed class TeamExecutable(TeamEntity team, ITeamEngine engine, IRepository<RunContext, RunRecord>? runRepository = null) : IExecutable
{
    public IExecutableDefinition Definition => team;

    public async ValueTask<ExecutorResult> ExecuteAsync(IReadOnlyDictionary<string, object?> args, RunContext context, CancellationToken ct = default)
    {
        var input = args.TryGetValue("input", out var v) && v is string s ? s : string.Empty;
        var runId = Guid.TryParse(context.RunId, out var rid) ? rid : Guid.NewGuid();

        // The run has ended since it was last called: the team is not run again, what it came to is given back from its run.
        // That is how the run that called it goes on once it is taken up again in its turn.
        if (context.IsResume && runRepository is not null && await runRepository.FindAsync(context.RunId, ct).ConfigureAwait(false) is { Status: RunStatus.Completed, Payload: { } payload })
        {
            var done = TeamProgress.FromRecord(payload);
            return ExecutorResult.FromText(done.FinalSynthesis ?? (done.Outputs.Count > 0 ? done.Outputs[^1].Content : null) ?? string.Empty);
        }

        var config = new Agentivity.Infrastructure.Team.TeamConfig(team, input, new TeamRunRequestOptions());
        var services = new TeamRunServices { RunId = runId, RunContext = context };

        var result = await engine.ExecuteAsync(config, services, ct).ConfigureAwait(false);

        var content = result.FinalSynthesis ?? (result.Outputs.Count > 0 ? result.Outputs[^1].Content : null) ?? string.Empty;

        return result.FinalState switch
        {
            RunStatus.Failed => ExecutorResult.FromText(result.Error ?? string.Empty) with { Status = ExecutableStatus.Failed },
            RunStatus.Paused => ExecutorResult.FromText(content) with { Status = ExecutableStatus.WaitingFor, FinishReason = "hil_pending", WaitingForToken = result.WaitingForToken },
            _ => ExecutorResult.FromText(content),
        };
    }
}
