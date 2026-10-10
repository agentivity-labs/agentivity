namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// A lightweight restore point persisted by the <see cref="GraphRuntime"/> when
/// <see cref="GraphPersistenceMode"/> is <see cref="GraphPersistenceMode.RestorePoint"/> or
/// <see cref="GraphPersistenceMode.Full"/>. Carries only the data needed to restore
/// runtime state from a <see cref="GraphSnapshot"/>.
/// </summary>
/// <param name="GraphId">The graph this restore point belongs to.</param>
/// <param name="RevisionCount">The revision counter at the time of the restore point.</param>
/// <param name="TaskStates">
/// Per-task status entries. Contains only tasks whose state has moved past
/// <see cref="GraphTaskRuntimeStatus.Pending"/> to reduce payload size.
/// </param>
public sealed record GraphRestorePoint(string GraphId, int RevisionCount, IReadOnlyList<GraphRestorePointTaskState> TaskStates);

/// <summary>
/// A single task's persisted state within a <see cref="GraphRestorePoint"/>.
/// </summary>
/// <param name="TaskId">The task identifier.</param>
/// <param name="RuntimeStatus">The status to restore.</param>
/// <param name="Artifact">The typed artifact produced on completion, if any.</param>
/// <param name="Output">The untyped completed output, if any.</param>
/// <param name="Error">The error message, if the task failed.</param>
/// <param name="SuspensionReason">The suspension reason, if the task suspended.</param>
/// <param name="WaitingForToken">The opaque resume token, if the task suspended.</param>
public sealed record GraphRestorePointTaskState(string TaskId, GraphTaskRuntimeStatus RuntimeStatus, GraphTaskArtifact? Artifact, object? Output, string? Error, string? SuspensionReason, string? WaitingForToken = null, IGraphTaskMetadata? Metadata = null);
