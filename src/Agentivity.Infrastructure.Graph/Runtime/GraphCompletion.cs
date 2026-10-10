namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Carries the information produced when a task batch completes inside
/// <see cref="GraphRuntime.CompleteTask"/>, <see cref="GraphRuntime.FailTask"/>,
/// or <see cref="GraphRuntime.SuspendTask"/>.
/// </summary>
/// <param name="CompletedTaskIds">Ids of the tasks whose states were updated in this call.</param>
/// <param name="Snapshot">Graph snapshot captured immediately after the update.</param>
public sealed record GraphCompletion(IReadOnlyList<string> CompletedTaskIds, GraphSnapshot Snapshot);
