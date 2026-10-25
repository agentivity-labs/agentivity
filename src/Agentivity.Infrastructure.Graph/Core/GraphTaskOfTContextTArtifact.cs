namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// A strongly-typed graph task that receives a shared <typeparamref name="TContext"/> and
/// produces a typed <typeparamref name="TArtifact"/> on successful completion.
/// </summary>
/// <typeparam name="TContext">
/// The shared execution context type for the graph. Injected by the host framework or
/// passed by a typed <c>GraphHost</c> variant.
/// </typeparam>
/// <typeparam name="TArtifact">
/// The artifact type produced by this task on successful completion.
/// Stored as <see cref="GraphTaskSnapshot.Artifact"/> in the runtime snapshot.
/// </typeparam>
/// <remarks>
/// <para>
/// This base class is a non-breaking extension of <see cref="GraphTask"/>. Existing tasks
/// that inherit from <see cref="GraphTask"/> directly continue to work without modification.
/// </para>
/// <para>
/// Subclasses override <see cref="ExecuteAsync(TContext, CancellationToken)"/> instead of
/// <see cref="GraphTask.ExecuteAsync(CancellationToken)"/>. The untyped override is sealed
/// and bridges to the typed variant automatically, converting the typed result to an
/// untyped <see cref="GraphTaskResult"/> so the existing <see cref="GraphRuntime"/> can
/// consume it unchanged.
/// </para>
/// </remarks>
public abstract class GraphTask<TContext, TArtifact> : GraphTask
    where TContext : GraphExecutionContext
    where TArtifact : GraphTaskArtifact
{
    /// <inheritdoc />
    protected GraphTask(string id, string[] dependsOn)
        : base(id, dependsOn) { }

    /// <summary>
    /// Executes the task with access to the shared context.
    /// Return <see cref="GraphTaskResult{TArtifact}.Failed"/> instead of throwing.
    /// </summary>
    public abstract ValueTask<GraphTaskResult<TArtifact>> ExecuteAsync(TContext context, CancellationToken cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Sealed — do not override. Provides the untyped bridge consumed by <see cref="GraphRuntime"/>.
    /// The context must be injected via the constructor or a property before <see cref="GraphRuntime"/>
    /// activates this task; or use a typed host that calls
    /// <see cref="ExecuteAsync(TContext, CancellationToken)"/> directly and stores the artifact.
    /// </remarks>
    public sealed override ValueTask<GraphTaskResult> ExecuteAsync(CancellationToken cancellationToken) => throw new InvalidOperationException($"{GetType().Name} must be executed via ExecuteAsync(TContext, CancellationToken). " + "Use a context-aware graph host.");
}
