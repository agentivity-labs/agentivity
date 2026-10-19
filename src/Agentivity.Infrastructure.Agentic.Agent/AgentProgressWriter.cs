using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Repository;

namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Single write point for the standalone-agent progress payload (EPIC RunRecord-unifié, Story 4) — called where an agent's
/// run starts (<c>RunsService</c>, <c>ExecutionService</c>) and where it is resumed (<c>StandaloneAgentResumableEngine</c>), so
/// none of them constructs the payload independently, and a resumed run gets its final content as a started one does.
/// </summary>
public static class AgentProgressWriter
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
