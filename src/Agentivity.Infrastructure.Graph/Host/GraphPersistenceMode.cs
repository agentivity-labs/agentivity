namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Controls how the <see cref="GraphHost"/> persists the state of a graph execution.
/// The mode is declared per graph in <see cref="GraphProfile"/> — not globally.
/// </summary>
public enum GraphPersistenceMode
{
    /// <summary>
    /// No persistence. The <see cref="IGraphCheckpointPort"/> is never called.
    /// Fastest execution path. Use for in-memory graphs such as LLMCompiler task batches.
    /// </summary>
    None,

    /// <summary>
    /// The <see cref="IGraphCheckpointPort"/> is called after each task completion.
    /// A crashed process can resume from the last completed task boundary.
    /// </summary>
    RestorePoint,

    /// <summary>
    /// The <see cref="IGraphCheckpointPort"/> is called before task activation and after task completion.
    /// Provides a full audit trail at the cost of additional store writes.
    /// </summary>
    Full,
}
