namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Convenience extensions on <see cref="IGraphHost"/> for common pause/idle patterns.
/// </summary>
public static class GraphHostExtensions
{
    /// <summary>
    /// Pauses scheduling for the graph and then waits until all currently in-progress
    /// tasks complete. After this call returns, the graph is fully idle and no new
    /// tasks will activate until <see cref="IGraphHost.ResumeAsync"/> is called.
    /// </summary>
    public static async ValueTask PauseAndWaitAsync(this IGraphHost host, GraphId graphId, CancellationToken cancellationToken = default)
    {
        await host.PauseAsync(graphId, cancellationToken).ConfigureAwait(false);
        await host.WaitForIdleAsync(graphId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pauses multiple graph executions in parallel and waits until all are idle.
    /// Useful for co-pausing a parent execution together with nested child executions
    /// (e.g. a workflow graph and its currently-running agent graph).
    /// </summary>
    public static async ValueTask CascadePauseAsync(this IGraphHost host, IEnumerable<GraphId> graphIds, CancellationToken cancellationToken = default)
    {
        var ids = graphIds.ToList();
        if (ids.Count == 0)
            return;

        // Pause all simultaneously so no new tasks start on any graph.
        await Task.WhenAll(ids.Select(id => host.PauseAsync(id, cancellationToken).AsTask())).ConfigureAwait(false);

        // Wait until every graph has drained its in-progress tasks.
        await Task.WhenAll(ids.Select(id => host.WaitForIdleAsync(id, cancellationToken).AsTask())).ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes multiple previously paused graph executions in parallel.
    /// </summary>
    public static async ValueTask CascadeResumeAsync(this IGraphHost host, IEnumerable<GraphId> graphIds)
    {
        var ids = graphIds.ToList();
        if (ids.Count == 0)
            return;

        await Task.WhenAll(ids.Select(id => host.ResumeAsync(id).AsTask())).ConfigureAwait(false);
    }
}
