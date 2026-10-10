namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// No-op <see cref="IGraphCheckpointPort"/> used by <see cref="GraphProfile.InMemory"/> graphs.
/// All operations complete immediately without allocating or writing anything.
/// </summary>
public sealed class NullGraphCheckpointPort : IGraphCheckpointPort
{
    /// <summary>Singleton instance.</summary>
    public static readonly NullGraphCheckpointPort Instance = new();

    private NullGraphCheckpointPort() { }

    /// <inheritdoc/>
    public ValueTask SaveAsync(GraphId graphId, GraphRestorePoint restorePoint, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<GraphRestorePoint?> LoadAsync(GraphId graphId, CancellationToken cancellationToken = default) => new((GraphRestorePoint?)null);

    /// <inheritdoc/>
    public ValueTask DeleteAsync(GraphId graphId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
