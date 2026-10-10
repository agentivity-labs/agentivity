namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Persistence contract for graph execution state.
/// Implementations are provided per-graph through <see cref="GraphProfile"/> and are
/// never shared globally — a workflow graph may use a file store while an LLMCompiler
/// graph uses <see cref="NullGraphCheckpointPort"/> without any configuration coupling.
/// </summary>
public interface IGraphCheckpointPort
{
    /// <summary>Persists a restore point for the specified graph.</summary>
    ValueTask SaveAsync(GraphId graphId, GraphRestorePoint restorePoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the most recent restore point for the specified graph,
    /// or returns <c>null</c> when no restore point exists.
    /// </summary>
    ValueTask<GraphRestorePoint?> LoadAsync(GraphId graphId, CancellationToken cancellationToken = default);

    /// <summary>Removes all stored state for the specified graph.</summary>
    ValueTask DeleteAsync(GraphId graphId, CancellationToken cancellationToken = default);
}
