namespace Agentivity.Infrastructure.Graph;

/// <summary>
/// Base class for a typed, mutable execution context shared across all tasks within a single
/// graph execution. Subclassed by domain-specific graphs to carry their own state.
/// </summary>
/// <remarks>
/// The <see cref="Scratchpad"/> is a thread-safe key/value note-pad. Tasks write structured
/// state into named slots (<see cref="GraphScratchpad.Set{T}"/>) and read it back
/// (<see cref="GraphScratchpad.Get{T}"/>). Writing to an existing key replaces its value.
/// When a task needs to accumulate values, it stores an accumulator object as the slot value.
/// </remarks>
public abstract class GraphExecutionContext
{
    /// <summary>The graph-level key/value scratchpad shared by all tasks in this execution.</summary>
    protected GraphScratchpad ScratchpadInstance { get; init; } = new();

    /// <summary>
    /// Returns the scratchpad cast to <typeparamref name="T"/>.
    /// Throws <see cref="InvalidCastException"/> when the scratchpad is not of the expected type.
    /// </summary>
    public T Scratchpad<T>()
        where T : GraphScratchpad => (T)ScratchpadInstance;
}
