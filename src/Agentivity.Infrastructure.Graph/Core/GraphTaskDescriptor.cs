namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// The serialisable, persistence-safe projection of a <see cref="GraphTask"/>.
/// Carries only the scheduling identity and caller-supplied metadata — no execution logic,
/// no captured dependencies, no runtime state.
/// </summary>
/// <remarks>
/// The <see cref="GraphHost"/> persists descriptors (not live task instances) when
/// <see cref="GraphProfile.Persistence"/> is not <see cref="GraphPersistenceMode.None"/>.
/// At restore time, the caller supplies an <see cref="IGraphTaskFactory"/> that rebuilds
/// live <see cref="GraphTask"/> instances from their descriptors.
///
/// All values stored in <see cref="Metadata"/> must be round-trip serialisable via
/// <c>System.Text.Json</c> with default options.
/// </remarks>
/// <param name="Id">Unique task identifier within the graph.</param>
/// <param name="DependsOn">Ids of tasks that must complete before this task becomes ready.</param>
/// <param name="Metadata">Caller-defined metadata used by <see cref="IGraphTaskFactory"/> to reconstruct the task.</param>
public sealed record GraphTaskDescriptor(string Id, IReadOnlyList<string> DependsOn, IReadOnlyDictionary<string, object?> Metadata)
{
    /// <summary>Creates a descriptor with an empty dependency list and empty metadata.</summary>
    public static GraphTaskDescriptor Simple(string id) => new(id, [], new Dictionary<string, object?>(StringComparer.Ordinal));
}
