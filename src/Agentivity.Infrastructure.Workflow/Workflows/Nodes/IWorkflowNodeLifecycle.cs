namespace Agentivity.Infrastructure.Workflows.Nodes;

/// <summary>
/// Optional lifecycle contract for workflow nodes.
///
/// Implemented by nodes that need to react to structural events in a workflow —
/// for example, reconfiguring their inputs when connected to a peer node.
///
/// The WorkflowEngine calls these methods at load time.
/// The workflow SDK calls them at construction time when building workflows programmatically.
///
/// Callers have no knowledge of what a node does in response — the node owns its own logic.
/// </summary>
public interface IWorkflowNodeLifecycle
{
    /// <summary>Called when this node is added to a workflow definition.</summary>
    void OnAdded(WorkflowEntity workflow);

    /// <summary>Called when this node is removed from a workflow definition.</summary>
    void OnRemoved(WorkflowEntity workflow);

    /// <summary>
    /// Called when a connection is established where this node is either source or target.
    /// <paramref name="peer"/> is the other node in the connection.
    /// </summary>
    void OnConnected(WorkflowConnection connection, WorkflowNodeDefinition peer, WorkflowEntity workflow);

    /// <summary>
    /// Called when a connection involving this node is removed.
    /// <paramref name="peer"/> is the other node that was connected.
    /// </summary>
    void OnDisconnected(WorkflowConnection connection, WorkflowNodeDefinition peer, WorkflowEntity workflow);

    /// <summary>Called once after the full workflow definition has been loaded.</summary>
    void OnWorkflowLoaded(WorkflowEntity workflow);
}
