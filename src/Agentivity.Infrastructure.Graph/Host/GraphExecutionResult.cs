namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Carries the final result of a graph execution as returned by
/// <see cref="IGraphHost.AwaitCompletionAsync"/>.
/// </summary>
/// <param name="GraphId">The identifier of the completed graph.</param>
/// <param name="FinalSnapshot">The graph state at the moment of completion.</param>
/// <param name="OverallStatus">
/// <see cref="GraphTaskStatus.Completed"/> when all tasks succeeded,
/// <see cref="GraphTaskStatus.Failed"/> when at least one task failed,
/// or <see cref="GraphTaskStatus.Suspended"/> when the graph was paused externally.
/// </param>
/// <param name="CompletedAt">UTC timestamp of graph completion.</param>
/// <param name="Outputs">
/// Per-task outputs keyed by task id. Only tasks with a non-null output are included.
/// </param>
/// <param name="WaitingForToken">
/// Opaque token from the first suspended task, propagated up through the execution hierarchy.
/// Null when <see cref="OverallStatus"/> is not <see cref="GraphTaskStatus.Suspended"/>.
/// </param>
public sealed record GraphExecutionResult(GraphId GraphId, GraphSnapshot FinalSnapshot, GraphTaskStatus OverallStatus, DateTimeOffset CompletedAt, IReadOnlyDictionary<string, object?> Outputs, string? WaitingForToken = null);
