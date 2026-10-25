using Agentivity.Domain.Run;
using Agentivity.Infrastructure.Core;
using Agentivity.Infrastructure.Execution.Executions;
using Agentivity.Infrastructure.Execution.Runs;
using Agentivity.Infrastructure.Repository;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// What the edition allows of the executions besides how many a month: how many run at the same time, how long one runs, and the
/// disk the workspace holds. A new execution is refused beyond the first and the last; one that runs too long is stopped as the
/// Cancel button stops it. Waiting for a person neither takes a place nor counts as running.
/// </summary>
public sealed class ExecutionLimits(IEdition edition, IRunRecordStore runs, TimeProvider clock)
{
    private long _diskBytes;
    private DateTimeOffset _diskReadAt = DateTimeOffset.MinValue;

    public const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>How often the disk is measured again: a measure goes through every file of the workspace.</summary>
    public static readonly TimeSpan DiskEvery = TimeSpan.FromMinutes(5);

    /// <summary>The executions running now: their first run, not the runs inside them (the members of a team), and not waiting.</summary>
    public async Task<IReadOnlyList<RunRecord>> RunningAsync(CancellationToken ct) =>
        [.. (await runs.ListByStateAsync(RunStatus.Running, ct).ConfigureAwait(false)).Where(r => string.IsNullOrEmpty(r.ParentRunId))];

    /// <summary>The disk the workspace holds now, in bytes (measured at most every <see cref="DiskEvery"/>).</summary>
    public long DiskBytes()
    {
        var now = clock.GetUtcNow();
        if (now - _diskReadAt >= DiskEvery)
        {
            _diskBytes = SizeOf(PathResolver.Resolve("_data"));
            _diskReadAt = now;
        }

        return _diskBytes;
    }

    /// <summary>Why a new execution may not start now (too many at once, the disk full), or null.</summary>
    public async Task<object?> RefusalAsync(CancellationToken ct)
    {
        if (edition.Limit(EditionLimits.MaxParallelRuns) is { } most && (await RunningAsync(ct).ConfigureAwait(false)).Count >= most)
            return new { error = "edition_limit", message = $"The {edition.Name} edition runs {most} executions at the same time: wait for one to finish, or choose a bigger plan." };
        if (edition.Limit(EditionLimits.MaxDiskGb) is { } gigabytes && DiskBytes() is var used && used >= gigabytes * Gigabyte)
            return new { error = "edition_limit", message = $"The {edition.Name} edition holds {gigabytes} GB, and the workspace holds {used / (double)Gigabyte:0.0} GB: delete what you no longer need, or choose a bigger plan." };
        return null;
    }

    private static long SizeOf(string folder)
    {
        if (!Directory.Exists(folder))
            return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // A file gone between listing and measuring.
            }
        }

        return total;
    }
}

/// <summary>
/// Stops an execution that has run longer than the edition allows (<see cref="EditionLimits.MaxRunMinutes"/>), as the Cancel
/// button does. The time counts while it runs without a break: an execution that waited for a person starts counting again.
/// </summary>
public sealed class ExecutionDurationWatch(
    IEdition edition,
    ExecutionLimits limits,
    IRepository<RunContext, RunRecord> runRepository,
    IExecutionRecordStore executions,
    TimeProvider clock,
    ILogger<ExecutionDurationWatch> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    // When each execution running now was first seen running, without a break since.
    private readonly Dictionary<string, DateTimeOffset> _runningSince = new(StringComparer.Ordinal);

    /// <summary>One look: the executions that have run too long are stopped; how many.</summary>
    public async Task<int> CheckAsync(CancellationToken ct)
    {
        var running = await limits.RunningAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        foreach (var gone in _runningSince.Keys.Except(running.Select(r => r.RunId)).ToList())
            _runningSince.Remove(gone);
        if (edition.Limit(EditionLimits.MaxRunMinutes) is not { } minutes)
        {
            _runningSince.Clear();
            return 0;
        }

        var stopped = 0;
        foreach (var run in running)
        {
            var since = _runningSince.TryGetValue(run.RunId, out var seen) ? seen : _runningSince[run.RunId] = now;
            if (now - since < TimeSpan.FromMinutes(minutes))
                continue;
            if (await runRepository.FindAsync(run.RunId, ct).ConfigureAwait(false) is { } context && context.TryCancel())
                await runRepository.AddAsync(context, ct).ConfigureAwait(false);
            if (run.ExecutionId is { } executionId && await executions.GetAsync(executionId, ct).ConfigureAwait(false) is { } execution)
                await executions.SaveAsync(execution with { State = "cancelled", UpdatedAt = now }, ct).ConfigureAwait(false);
            _runningSince.Remove(run.RunId);
            stopped++;
            logger.LogWarning("Execution {ExecutionId} stopped: it ran longer than the {Minutes} minutes the {Edition} edition allows", run.ExecutionId, minutes, edition.Name);
        }

        return stopped;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "The duration of the executions could not be checked");
            }

            try
            {
                await Task.Delay(Every, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
