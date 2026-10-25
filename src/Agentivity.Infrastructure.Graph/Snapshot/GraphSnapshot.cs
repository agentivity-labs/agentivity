namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// A point-in-time view of the state of an entire graph execution captured by
/// <see cref="GraphRuntime.BuildSnapshot"/>.
/// Each <see cref="GraphTask"/> in <see cref="Tasks"/> carries its current
/// <see cref="GraphTask.State"/> at the moment the snapshot was taken.
/// </summary>
/// <param name="GraphId">The identifier of the graph this snapshot belongs to.</param>
/// <param name="RevisionCount">
/// Monotonically increasing counter incremented on each plan replacement.
/// </param>
/// <param name="Tasks">
/// Ordered task snapshots matching the original task registration order. Each entry is
/// an immutable <see cref="GraphTaskSnapshot"/> that includes the derived
/// <see cref="GraphTaskSnapshot.SchedulingStatus"/> and <see cref="GraphTaskSnapshot.BlockingReason"/>
/// so that consumers do not need to recompute scheduling semantics.
/// </param>
public sealed record GraphSnapshot(string GraphId, int RevisionCount, IReadOnlyList<GraphTaskSnapshot> Tasks);
