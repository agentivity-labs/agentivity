using Agentivity.Infrastructure.Interactions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentivity.Host.Runs;

/// <summary>
/// Startup recovery (EPIC durable-execution, Story 3): scans the durable run records once at
/// boot via <see cref="DurableRunService.RecoverAllAsync"/> — waiting runs are re-armed so
/// pending HIL questions remain answerable, runs orphaned by the previous process are marked
/// Interrupted (manually re-enterable via <c>POST /api/v1/runs/{runId}/recover</c>).
/// </summary>
internal sealed class RunRecoveryService(DurableRunService durableRunService, ILogger<RunRecoveryService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await durableRunService.RecoverAllAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Startup run recovery done: {Rearmed} re-armed, {Interrupted} interrupted.", report.Rearmed, report.Interrupted);
        }
        catch (Exception ex)
        {
            // Recovery must never block the host from starting.
            logger.LogError(ex, "Startup run recovery failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
