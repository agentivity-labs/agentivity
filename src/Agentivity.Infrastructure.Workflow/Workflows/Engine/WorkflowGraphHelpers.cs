using Agentivity.Infrastructure.Workflows.Nodes;
using Microsoft.Extensions.Logging;

namespace Agentivity.Infrastructure.Workflows;

public sealed partial class WorkflowEngine
{
    private bool ShouldEnqueueNode(WorkflowEntity definition, string nodeId, Guid runId)
    {
        var (traversed, total) = GetTraversedConnectionsCount(definition, nodeId, runId);

        if (total == 0)
        {
            return true;
        }

        var nodeDef = definition.FindNode(nodeId);
        if (nodeDef is not null && _nodeFactory.GetPrototype(nodeDef.NodeType) is { } WorkflowNodeDefinition)
        {
            if (WorkflowNodeDefinition is IPartialExecutionNode partialNode)
            {
                var shouldExecute = partialNode.ShouldExecuteWithTraversedConnections(traversed, total);
                if (shouldExecute)
                {
                    _logger.LogDebug("Partial execution node '{NodeId}' ready to enqueue: {Traversed}/{Total} connections traversed", nodeId, traversed, total);
                }
                return shouldExecute;
            }
        }

        return traversed >= total;
    }

    private (int traversedCount, int totalCount) GetTraversedConnectionsCount(WorkflowEntity definition, string nodeId, Guid runId)
    {
        var incomingConnections = definition.Connections.Where(c => c.To == nodeId && !string.Equals(c.FromPort, "resource", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.ToPort, "resource", StringComparison.OrdinalIgnoreCase)).ToList();

        var totalCount = incomingConnections.Count;
        var entry = GetRun(runId.ToString("D"));

        if (entry is null || entry.Transitions.Count == 0)
        {
            return (0, totalCount);
        }

        var traversedCount = incomingConnections.Count(conn => entry.Transitions.Any(h => string.Equals(h.FromStepId, conn.From, StringComparison.OrdinalIgnoreCase) && string.Equals(h.ToStepId, conn.To, StringComparison.OrdinalIgnoreCase) && h.Traversed));

        return (traversedCount, totalCount);
    }

    private static IReadOnlyList<WorkflowConnection> GetOutgoingConnections(WorkflowEntity definition, string fromNodeId) => definition.Connections.Where(c => string.Equals(c.From, fromNodeId, StringComparison.OrdinalIgnoreCase) && !IsResourceConnection(c)).ToList();

    private static bool IsResourceConnection(WorkflowConnection connection) => string.Equals(connection.FromPort, "resource", StringComparison.OrdinalIgnoreCase) || string.Equals(connection.ToPort, "resource", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, IReadOnlyList<string>> BuildPredecessorMap(WorkflowEntity definition)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in definition.Connections)
        {
            if (string.Equals(connection.FromPort, "resource", StringComparison.OrdinalIgnoreCase) || string.Equals(connection.ToPort, "resource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!map.TryGetValue(connection.To, out var preds))
            {
                preds = new List<string>();
                map[connection.To] = preds;
            }
            preds.Add(connection.From);
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in map)
        {
            result[kvp.Key] = kvp.Value.AsReadOnly();
        }
        return result;
    }
}
