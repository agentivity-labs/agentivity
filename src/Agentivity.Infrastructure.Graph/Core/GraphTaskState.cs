namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Immutable runtime state for a single <see cref="GraphTask"/>.
/// Owned by the task itself and replaced (via <c>with</c>) by the <see cref="GraphRuntime"/>
/// on every lifecycle transition.
/// </summary>
public sealed record GraphTaskState
{
    /// <summary>Current lifecycle status. Starts as <see cref="GraphTaskRuntimeStatus.Pending"/>.</summary>
    public GraphTaskRuntimeStatus RuntimeStatus { get; init; } = GraphTaskRuntimeStatus.Pending;

    /// <summary>Typed artifact produced when the task completed via a typed execution path. Null otherwise.</summary>
    public GraphTaskArtifact? Artifact { get; init; }

    /// <summary>Untyped output produced when the task completed via <see cref="GraphTaskResult.Completed(object?)"/>. Null otherwise.</summary>
    public object? Output { get; init; }

    /// <summary>Error message when the task failed. Null otherwise.</summary>
    public string? Error { get; init; }

    /// <summary>Suspension reason when the task is suspended. Null otherwise.</summary>
    public string? SuspensionReason { get; init; }

    /// <summary>
    /// Opaque token identifying what this task is waiting for (HIL request id, webhook id…).
    /// Propagated up through the entire execution hierarchy to the root handler.
    /// Null when not suspended.
    /// </summary>
    public string? WaitingForToken { get; init; }

    /// <summary>Internal reason text when the task was ignored due to a failed or ignored dependency.</summary>
    public string? IgnoredBecause { get; init; }
}
