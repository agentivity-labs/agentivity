namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Carries the information produced by <see cref="GraphRuntime.TryActivateNext"/>.
/// Describes which tasks were activated and the graph state at the moment of activation.
/// </summary>
/// <param name="ActivatedTasks">Tasks moved to <see cref="GraphTaskRuntimeStatus.InProgress"/>.</param>
/// <param name="Snapshot">Graph snapshot captured immediately after activation.</param>
public sealed record GraphActivation(IReadOnlyList<GraphTask> ActivatedTasks, GraphSnapshot Snapshot);
