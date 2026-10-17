using Agentivity.Infrastructure.Http;
using Agentivity.Infrastructure.Plugins;
using Agentivity.Infrastructure.Workflow.Services.WorkflowManagement;
using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentivity.Infrastructure.Workflow.Catalog.Endpoints;

/// <summary>
/// Exposes POST /nodes/reconfigure — triggers IWorkflowNodeLifecycle.OnConnected for a node,
/// calls IWorkflowNodeReconfigurable.OnReconfigure, fires per-input onChange callbacks,
/// and returns the updated input descriptors + diff without persisting the workflow.
///
/// Intended for editor use: when a triggersReconfigure input changes (e.g. agent_mode),
/// the frontend calls this endpoint to get the refreshed input list for the node.
/// </summary>
public sealed class NodeReconfigureEndpointContributor : IEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        _ = endpoints
            .MapV1()
            .MapPost(
                "/nodes/reconfigure",
                async (NodeReconfigureRequest request, WorkflowService workflowService, WorkflowEngine workflowEngine, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(request.WorkflowId))
                        return Results.BadRequest(new { error = "workflowId is required." });

                    if (string.IsNullOrWhiteSpace(request.NodeId))
                        return Results.BadRequest(new { error = "nodeId is required." });

                    WorkflowEntity? workflow = await workflowService.GetByIdAsync(request.WorkflowId, ct).ConfigureAwait(false);
                    if (workflow is null)
                        return Results.NotFound(new { error = $"Workflow '{request.WorkflowId}' not found." });

                    WorkflowNodeDefinition? WorkflowNodeDefinition = workflow.FindNode(request.NodeId);
                    if (WorkflowNodeDefinition is null)
                        return Results.NotFound(new { error = $"Node '{request.NodeId}' not found in workflow '{request.WorkflowId}'." });

                    Node? catalogNode = workflowEngine.CreateNode(WorkflowNodeDefinition.NodeType);
                    if (catalogNode is null)
                        return Results.NotFound(new { error = $"Node type '{WorkflowNodeDefinition.NodeType}' is not registered." });

                    if (catalogNode is not BaseNode baseNode)
                        return Results.Problem($"Node '{WorkflowNodeDefinition.NodeType}' does not support metadata.");

                    // Snapshot before
                    var before = baseNode.GetInputsWithMetadata().ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

                    // 1. Fire OnConnected for all connections involving this node (existing behaviour)
                    if (catalogNode is IWorkflowNodeLifecycle lifecycle)
                    {
                        foreach (WorkflowConnection connection in workflow.Connections)
                        {
                            string? peerId = null;
                            if (string.Equals(connection.To, request.NodeId, StringComparison.OrdinalIgnoreCase))
                                peerId = connection.From;
                            else if (string.Equals(connection.From, request.NodeId, StringComparison.OrdinalIgnoreCase))
                                peerId = connection.To;

                            if (peerId is null)
                                continue;

                            WorkflowNodeDefinition? peerWorkflowNode = workflow.FindNode(peerId);
                            if (peerWorkflowNode is null)
                                continue;

                            lifecycle.OnConnected(connection, peerWorkflowNode, workflow);
                        }
                    }

                    // 2. If current values are provided, call OnReconfigure and per-input callbacks
                    if (request.CurrentValues is not null)
                    {
                        if (catalogNode is IWorkflowNodeReconfigurable reconfigurable)
                            reconfigurable.OnReconfigure(WorkflowNodeDefinition, request.CurrentValues);

                        baseNode.FireReconfigureCallbacks(request.CurrentValues);
                    }

                    // Snapshot after, build diff, return response
                    IReadOnlyList<NodeInputDescriptor> after = baseNode.GetInputsWithMetadata();
                    NodeInputDiff diff = BuildDiff(before, after, request.CurrentValues);

                    return Results.Ok(new NodeReconfigureResponse(after, diff));
                }
            )
            .WithSummary("Refresh the inputs of a workflow node");
    }

    private static NodeInputDiff BuildDiff(IReadOnlyDictionary<string, NodeInputDescriptor> before, IReadOnlyList<NodeInputDescriptor> after, IReadOnlyDictionary<string, object?>? currentValues = null)
    {
        var afterKeys = after.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var beforeKeys = before.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        NodeInputDescriptor[] added = after.Where(i => !beforeKeys.Contains(i.Name)).ToArray();
        NodeInputDescriptor[] removed = before.Values.Where(i => !afterKeys.Contains(i.Name)).ToArray();
        NodeInputDescriptor[] modified = after.Where(i => beforeKeys.Contains(i.Name) && !InputEquals(before[i.Name], i)).ToArray();

        // Keys present in the saved JSON that no longer exist in any node input definition.
        // The frontend should strip these to avoid phantom tabs / unknown fields.
        string[] stale = currentValues is null ? [] : currentValues.Keys.Where(k => !afterKeys.Contains(k)).ToArray();

        return new NodeInputDiff(added, removed, modified, stale);
    }

    private static bool InputEquals(NodeInputDescriptor a, NodeInputDescriptor b) => a.Type == b.Type && a.Description == b.Description && a.EditorMode == b.EditorMode && a.TriggersReconfigure == b.TriggersReconfigure;
}

public sealed record NodeReconfigureRequest(string WorkflowId, string NodeId, IReadOnlyDictionary<string, object?>? CurrentValues = null);

public sealed record NodeReconfigureResponse(IReadOnlyList<NodeInputDescriptor> Inputs, NodeInputDiff? Diff = null);

public sealed record NodeInputDiff(
    IReadOnlyList<NodeInputDescriptor> Added,
    IReadOnlyList<NodeInputDescriptor> Removed,
    IReadOnlyList<NodeInputDescriptor> Modified,
    /// <summary>
    /// Input keys present in the saved workflow JSON that are no longer declared by the node.
    /// The frontend should remove these values to avoid phantom tabs or unknown fields.
    /// </summary>
    IReadOnlyList<string> Stale
);
