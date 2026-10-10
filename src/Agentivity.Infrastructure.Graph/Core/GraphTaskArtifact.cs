namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Base class for the typed artifact produced by a graph task upon successful completion.
/// Subclassed by domain-specific graphs to carry their own result payload.
/// </summary>
/// <remarks>
/// <para>
/// An artifact is the typed equivalent of <see cref="GraphTaskResult.Output"/> for graphs
/// that use the generic <see cref="GraphTask{TContext,TArtifact}"/> base class. It replaces
/// the untyped <c>object?</c> surface with a strongly-typed, inspectable result.
/// </para>
/// <para>
/// When a task produces an artifact, it is stored in <see cref="GraphTaskSnapshot.Artifact"/>
/// by the graph runtime and remains accessible for the lifetime of the graph snapshot.
/// </para>
/// <para>
/// The three virtual properties — <see cref="Observation"/>, <see cref="Error"/>, and
/// <see cref="ValidationSignal"/> — provide a queryable surface without requiring callers
/// to downcast to domain-specific artifact types.
/// </para>
/// </remarks>
public abstract record GraphTaskArtifact
{
    /// <summary>
    /// Human-readable observation or synthesized result text produced by the task.
    /// Override in domain artifacts that carry result text.
    /// </summary>
    public virtual string? Observation => null;

    /// <summary>
    /// Error or failure description produced by the task, if any.
    /// Override in domain artifacts that carry error detail.
    /// </summary>
    public virtual string? Error => null;

    /// <summary>
    /// Structured validation signal emitted by the task (e.g. a quality gate verdict).
    /// Override in domain artifacts that carry structured feedback.
    /// </summary>
    public virtual string? ValidationSignal => null;
}
