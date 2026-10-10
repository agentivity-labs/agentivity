using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Options;
using Agentivity.Infrastructure.Repository;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ExecutionContext = Agentivity.Infrastructure.Execution.Executions.ExecutionContext;

namespace Agentivity.Infrastructure.Execution;

/// <summary>
/// Background service that evicts terminated (completed/failed/cancelled) executions — and,
/// since a run is only ever the content of the execution that owns it, cascades the eviction to
/// every run the execution references — from the in-memory <see cref="IRepository{TEntity,TRecord}"/>
/// stores after a configurable TTL. Works for all run types — workflow, agent, team — without
/// knowing their backing stores.
/// </summary>
public sealed class ExecutionCleanupService : BackgroundService
{
    private readonly WorkflowRunRetentionOptions _options;
    private readonly IRepository<RunContext, RunRecord> _runRepository;
    private readonly IRepository<ExecutionContext, ExecutionRecord> _executionRepository;
    private readonly ILogger<ExecutionCleanupService> _logger;

    public ExecutionCleanupService(
        IOptions<WorkflowRunRetentionOptions> options,
        IRepository<RunContext, RunRecord> runRepository,
        IRepository<ExecutionContext, ExecutionRecord> executionRepository,
        ILogger<ExecutionCleanupService> logger
    )
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _runRepository = runRepository ?? throw new ArgumentNullException(nameof(runRepository));
        _executionRepository = executionRepository ?? throw new ArgumentNullException(nameof(executionRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Run registry cleanup started. Interval: {Interval} min, TTL: completed={Completed}d failed={Failed}d cancelled={Cancelled}d max={Max}",
            _options.CleanupIntervalMinutes,
            _options.KeepCompletedDays,
            _options.KeepFailedDays,
            _options.KeepCancelledDays,
            _options.MaxRunsToKeep
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_options.CleanupIntervalMinutes), stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                var evicted = await EvictAsync(stoppingToken);
                if (evicted > 0)
                    _logger.LogInformation("Evicted {Count} expired run(s) from registry", evicted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during run registry cleanup");
            }
        }

        _logger.LogInformation("Run registry cleanup stopped");
    }

    /// <summary>Internal for direct unit testing — avoids waiting on the real cleanup interval.</summary>
    internal async Task<int> EvictAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var retentionPeriod = TimeSpan.FromDays(Math.Max(Math.Max(_options.KeepCompletedDays, _options.KeepFailedDays), _options.KeepCancelledDays));
        var evicted = 0;

        // Executions are the parent unit — a run only ever exists as the content of the execution
        // that started it. Evicting the execution first and cascading to its runs keeps the two
        // stores from drifting apart (an execution can never survive as an empty shell, and a run
        // is never orphaned by an execution that vanished around it).
        var expiredExecutions = await _executionRepository.FindWhereAsync(e => e.State is "completed" or "failed" or "cancelled" && now >= e.UpdatedAt.Add(retentionPeriod), ct);
        foreach (var execution in expiredExecutions)
        {
            foreach (var runId in execution.RunIds)
            {
                await _runRepository.DeleteAsync(runId, ct);
                evicted++;
            }
            await _executionRepository.DeleteAsync(execution.ExecutionId, ct);
            evicted++;
        }

        // Safety net for runs whose execution is still active (e.g. a long-lived standalone agent
        // conversation) but whose own run history has grown past the cap.
        var all = await _runRepository.FindAllAsync(ct);
        if (all.Count > _options.MaxRunsToKeep)
        {
            var oldest = all.OrderBy(c => c.CreatedAt).Take(all.Count - _options.MaxRunsToKeep);
            foreach (var context in oldest)
            {
                await _runRepository.DeleteAsync(context.RunId, ct);
                evicted++;
            }
        }

        return evicted;
    }
}
