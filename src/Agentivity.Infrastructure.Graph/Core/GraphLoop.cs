namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Factory helpers for common loop patterns over <see cref="GraphTask"/> collections.
/// </summary>
public static class GraphLoop
{
    /// <summary>
    /// Produces one independent <see cref="GraphTask"/> per item — all iterations can
    /// execute in parallel (subject to <c>GlobalMaxConcurrentTasks</c>).
    /// </summary>
    public static IReadOnlyList<TTask> ForEachParallel<T, TTask>(IEnumerable<T> items, Func<T, int, TTask> taskFactory)
        where TTask : GraphTask
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(taskFactory);

        var result = new List<TTask>();
        var index = 0;
        foreach (var item in items)
            result.Add(taskFactory(item, index++));
        return result;
    }

    /// <summary>
    /// Produces one <see cref="GraphTask"/> per item chained sequentially — each iteration
    /// depends on the previous one completing before it activates.
    /// </summary>
    public static IReadOnlyList<TTask> ForEachSequential<T, TTask>(IEnumerable<T> items, Func<T, int, string?, TTask> taskFactory)
        where TTask : GraphTask
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(taskFactory);

        var result = new List<TTask>();
        string? previousId = null;
        var index = 0;
        foreach (var item in items)
        {
            result.Add(taskFactory(item, index++, previousId));
            previousId = result[^1].Id;
        }
        return result;
    }
}
