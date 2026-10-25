namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// Marker interface for nodes that can execute when only some incoming connections have been traversed.
/// These nodes don't wait for all incoming connections to be traversed before executing.
/// Useful for conditional branch merging (e.g., FirstOf, cache-or-compute patterns).
/// </summary>
/// <remarks>
/// By default, the workflow engine waits for ALL incoming connections to a node
/// to be traversed before executing it. This causes deadlocks when conditional branches
/// converge (e.g., if/else branches merging into a single node where only one branch executes).
///
/// Nodes implementing this interface can signal when they're ready to execute
/// with only a subset of their incoming connections traversed.
/// </remarks>
public interface IPartialExecutionNode
{
    /// <summary>
    /// Determines if the node should execute now based on how many incoming connections have been traversed.
    /// Called by the workflow engine whenever a predecessor completes and traverses a connection to this node.
    /// </summary>
    /// <param name="traversedConnectionsCount">Number of incoming connections that have been traversed</param>
    /// <param name="totalConnectionsCount">Total number of non-resource incoming connections to this node</param>
    /// <returns>True if the node should execute now, false to wait for more connections to be traversed</returns>
    /// <example>
    /// // FirstOf executes as soon as ANY connection is traversed (at least one branch completed)
    /// public bool ShouldExecuteWithTraversedConnections(
    ///     int traversedConnectionsCount,
    ///     int totalConnectionsCount)
    /// {
    ///     return traversedConnectionsCount > 0;
    /// }
    /// </example>
    bool ShouldExecuteWithTraversedConnections(int traversedConnectionsCount, int totalConnectionsCount);
}
