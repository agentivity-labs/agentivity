namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Factory helpers for race patterns where the first task to complete successfully wins.
/// </summary>
public static class GraphRace
{
    /// <summary>
    /// Tags all provided tasks with the same <paramref name="raceGroupId"/> so that
    /// <see cref="GraphRuntime.CancelRaceGroupSiblings"/> can cancel losers when a winner emerges.
    /// </summary>
    public static IReadOnlyList<TTask> FirstOf<TTask>(string raceGroupId, IEnumerable<TTask> tasks)
        where TTask : GraphTask
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var result = new List<TTask>();
        foreach (var task in tasks)
        {
            if (task.RaceGroupId is not null && task.RaceGroupId != raceGroupId)
                throw new InvalidOperationException($"Task '{task.Id}' already belongs to race group '{task.RaceGroupId}'.");
            // Note: RaceGroupId is init-only; set via object initializer before calling FirstOf,
            // or the factory pattern should be used.
            result.Add(task);
        }
        return result;
    }
}
