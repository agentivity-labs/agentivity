using System.Text.Json;
using Agentivity.Infrastructure.Storage.Workspace;

namespace Agentivity.Infrastructure.Identity;

/// <summary>
/// How many executions were started each month (in UTC), kept as a domain of the workspace: the edition limits it
/// (<see cref="EditionLimits.MaxRunsPerMonth"/>), and the administration shows it. Only the start of an execution counts, not
/// its steps, nor what it calls, nor a person answering it.
/// </summary>
public sealed class MonthlyRuns(IWorkspaceObjectStore store, TimeProvider clock)
{
    public const string MonthType = "usage-month";
    public static readonly IReadOnlyList<string> ObjectTypes = [MonthType];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One instance runs on one machine: a start is counted under this gate, so two starts never take the same last place.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record Month(string Id, int Runs);

    /// <summary>The executions of this month and of the one before (UTC, 2026-11), as they are counted.</summary>
    public async Task<IReadOnlyList<(string Month, int Runs)>> RecentAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var months = new List<(string, int)>();
        foreach (var at in new[] { now.AddMonths(-1), now })
            months.Add((at.UtcDateTime.ToString("yyyy-MM"), (await ReadAsync(IdOf(at), cancellationToken).ConfigureAwait(false))?.Runs ?? 0));
        return months;
    }

    /// <summary>The executions started this month.</summary>
    public async Task<int> ThisMonthAsync(CancellationToken cancellationToken = default) => (await ReadAsync(IdOf(clock.GetUtcNow()), cancellationToken).ConfigureAwait(false))?.Runs ?? 0;

    /// <summary>
    /// Counts one more start this month, unless it would go beyond <paramref name="most"/> (no limit when null). False when it
    /// would: then nothing is counted.
    /// </summary>
    public async Task<bool> TryStartAsync(long? most, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow();
            var id = IdOf(now);
            var runs = (await ReadAsync(id, cancellationToken).ConfigureAwait(false))?.Runs ?? 0;
            if (most is { } limit && runs >= limit)
                return false;

            var saved = await store.SaveAsync(WorkspaceObjectEnvelope.CreateJson(MonthType, id, id, JsonSerializer.Serialize(new Month(id, runs + 1), Json), new WorkspaceObjectMetadata(now, now)), cancellationToken).ConfigureAwait(false);
            if (!saved.Success)
                throw new InvalidOperationException($"The executions of the month could not be counted: {saved.Error}");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string IdOf(DateTimeOffset at) => $"runs-{at.UtcDateTime:yyyy-MM}";

    private async Task<Month?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        var kept = await store.GetAsync(MonthType, id, cancellationToken).ConfigureAwait(false);
        return kept.Success && kept.Value is not null ? JsonSerializer.Deserialize<Month>(kept.Value.Payload, Json) : null;
    }
}
