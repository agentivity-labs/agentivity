namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The outcome of a single <see cref="GraphTask.ExecuteAsync"/> call.
/// Carries the final status, an optional output payload, and an optional error or suspension reason.
/// </summary>
/// <remarks>
/// Use the static factory methods to construct results — do not use the primary constructor directly.
/// The <see cref="Output"/> property is only meaningful when <see cref="Status"/> is
/// <see cref="GraphTaskStatus.Completed"/>. The <see cref="Error"/> property is only meaningful
/// when <see cref="Status"/> is <see cref="GraphTaskStatus.Failed"/>. The <see cref="SuspensionReason"/>
/// is only meaningful when <see cref="Status"/> is <see cref="GraphTaskStatus.Suspended"/>.
/// </remarks>
public sealed record GraphTaskResult
{
    private GraphTaskResult(GraphTaskStatus status, object? output, string? error, string? suspensionReason, string? WaitingForToken = null)
    {
        Status = status;
        Output = output;
        Error = error;
        SuspensionReason = suspensionReason;
        // 'this.': the parameter has the name of the property, and without it the token was assigned to itself and lost.
        this.WaitingForToken = WaitingForToken;
    }

    /// <summary>The final status of the task execution.</summary>
    public GraphTaskStatus Status { get; }

    /// <summary>
    /// The output payload produced by the task. Only meaningful when
    /// <see cref="Status"/> is <see cref="GraphTaskStatus.Completed"/>.
    /// </summary>
    public object? Output { get; }

    /// <summary>
    /// The error message describing the failure. Only meaningful when
    /// <see cref="Status"/> is <see cref="GraphTaskStatus.Failed"/>.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// A short machine-readable reason explaining why the task suspended.
    /// Only meaningful when <see cref="Status"/> is <see cref="GraphTaskStatus.Suspended"/>.
    /// </summary>
    public string? SuspensionReason { get; }

    /// <summary>
    /// Opaque token identifying what this task is waiting for (HIL request id, webhook id…).
    /// Propagated up through the entire execution hierarchy to the root handler.
    /// Null when not suspended.
    /// </summary>
    public string? WaitingForToken { get; }

    /// <summary>Creates a successful result carrying the given output payload.</summary>
    public static GraphTaskResult Completed(object? output = null) => new(GraphTaskStatus.Completed, output, error: null, suspensionReason: null);

    /// <summary>Creates a failure result carrying the given error message.</summary>
    public static GraphTaskResult Failed(string error) => new(GraphTaskStatus.Failed, output: null, error: error, suspensionReason: null);

    /// <summary>
    /// Creates a suspension result carrying a short machine-readable reason and an opaque resume token.
    /// </summary>
    public static GraphTaskResult Suspended(string reason, string? token = null) => new(GraphTaskStatus.Suspended, output: null, error: null, suspensionReason: reason, WaitingForToken: token);
}

/// <summary>
/// A strongly-typed variant of <see cref="GraphTaskResult"/> for tasks that produce a
/// <typeparamref name="TArtifact"/> upon completion.
/// </summary>
/// <typeparam name="TArtifact">The type of artifact produced on successful completion.</typeparam>
/// <remarks>
/// Use the static factory methods to construct results. The untyped <see cref="GraphTaskResult"/>
/// base remains the contract consumed by <see cref="GraphRuntime"/> — this typed variant is a
/// convenience surface for <see cref="GraphTask{TContext,TArtifact}"/> implementations.
/// </remarks>
public sealed record GraphTaskResult<TArtifact>
    where TArtifact : GraphTaskArtifact
{
    private GraphTaskResult(GraphTaskStatus status, TArtifact? artifact, string? error, string? suspensionReason, string? WaitingForToken = null)
    {
        Status = status;
        Artifact = artifact;
        Error = error;
        SuspensionReason = suspensionReason;
        // 'this.': the parameter has the name of the property, and without it the token was assigned to itself and lost.
        this.WaitingForToken = WaitingForToken;
    }

    /// <summary>The final status of the task execution.</summary>
    public GraphTaskStatus Status { get; }

    /// <summary>The typed artifact produced on successful completion. Null when not completed.</summary>
    public TArtifact? Artifact { get; }

    /// <summary>The error message describing the failure. Null when not failed.</summary>
    public string? Error { get; }

    /// <summary>A machine-readable suspension reason. Null when not suspended.</summary>
    public string? SuspensionReason { get; }

    /// <summary>Opaque resume token. Null when not suspended.</summary>
    public string? WaitingForToken { get; }

    /// <summary>Creates a successful result carrying the given typed artifact.</summary>
    public static GraphTaskResult<TArtifact> Completed(TArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new(GraphTaskStatus.Completed, artifact, error: null, suspensionReason: null);
    }

    /// <summary>Creates a failure result carrying the given error message.</summary>
    public static GraphTaskResult<TArtifact> Failed(string error) => new(GraphTaskStatus.Failed, artifact: null, error: error, suspensionReason: null);

    /// <summary>Creates a suspension result carrying a short machine-readable reason and an opaque resume token.</summary>
    public static GraphTaskResult<TArtifact> Suspended(string reason, string? token = null) => new(GraphTaskStatus.Suspended, artifact: null, error: null, suspensionReason: reason, WaitingForToken: token);

    /// <summary>
    /// Converts this typed result to an untyped <see cref="GraphTaskResult"/> for consumption
    /// by the graph runtime, carrying the artifact as the <c>Output</c> payload.
    /// </summary>
    public GraphTaskResult ToUntyped() =>
        Status switch
        {
            GraphTaskStatus.Completed => GraphTaskResult.Completed(Artifact),
            GraphTaskStatus.Failed => GraphTaskResult.Failed(Error!),
            GraphTaskStatus.Suspended => GraphTaskResult.Suspended(SuspensionReason!, WaitingForToken),
            _ => throw new InvalidOperationException($"Unexpected status: {Status}"),
        };
}
