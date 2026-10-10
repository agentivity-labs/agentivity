using Agentivity.Infrastructure.Agentic.Agent;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Host.Runs;

/// <summary>
/// Single write point for the standalone-agent progress payload (EPIC RunRecord-unifié, Story 4) —
/// called by both <see cref="RunsService"/> and <see cref="Agentivity.Host.Executions.ExecutionService"/>
/// so the two start-run entry points never construct the payload independently (the duplication
/// this story removes).
/// </summary>
internal static class AgentProgressWriter
{
    public static async ValueTask SetPayloadAsync(IRepository<RunContext, RunRecord>? runRepository, string runId, string input, string? content = null, CancellationToken ct = default)
    {
        if (runRepository is null) return;
        var ctx = await runRepository.FindAsync(runId, ct).ConfigureAwait(false);
        if (ctx is null) return;
        ctx.SetPayload("agent", new AgentProgress(input, content).ToRecord());
        await runRepository.AddAsync(ctx, ct).ConfigureAwait(false);
    }
}
