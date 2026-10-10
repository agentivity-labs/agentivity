using System.Text.Json;
using Agentivity.Domain.Workflow;
using Agentivity.Infrastructure.Workflows.ControlFlow;
using Agentivity.Infrastructure.Workflows.Nodes;

namespace Agentivity.Infrastructure.Workflows.Validation;

/// <summary>
/// Validates workflow definitions and detects common issues and anti-patterns.
/// </summary>
public sealed class WorkflowValidator
{
    private readonly IReadOnlyDictionary<string, Node> _availableNodes;

    /// <summary>
    /// Creates a new workflow validator with access to available node types.
    /// </summary>
    /// <param name="availableNodes">Dictionary of available nodes by node type</param>
    public WorkflowValidator(IEnumerable<Node> availableNodes)
    {
        _availableNodes = (availableNodes ?? throw new ArgumentNullException(nameof(availableNodes))).ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates a workflow and returns detected issues.
    /// </summary>
    public WorkflowValidationResult Validate(WorkflowEntity workflow)
    {
        var issues = new List<WorkflowValidationIssue>();

        // Run all validation checks
        issues.AddRange(ValidateMultipleBranchesWithoutJoin(workflow));
        issues.AddRange(ValidateOrphanedNodes(workflow));
        issues.AddRange(ValidateResourceConnections(workflow));
        issues.AddRange(ValidatePotentialDeadlocks(workflow));
        issues.AddRange(ValidateConditionalBranches(workflow));
        issues.AddRange(ValidateCircularDependencies(workflow));

        return new WorkflowValidationResult { Issues = issues };
    }

    /// <summary>
    /// Detects nodes that receive multiple inputs from conditional branches without a Join node.
    /// </summary>
    private List<WorkflowValidationIssue> ValidateMultipleBranchesWithoutJoin(WorkflowEntity workflow)
    {
        var issues = new List<WorkflowValidationIssue>();

        foreach (var node in workflow.Nodes)
        {
            // Get all incoming connections to this node
            var incomingConnections = workflow.Connections.Where(c => c.To == node.Id).ToList();

            if (incomingConnections.Count <= 1)
                continue;

            // Get source nodes
            var sourceNodes = incomingConnections.Select(c => workflow.Nodes.FirstOrDefault(n => n.Id == c.From)).Where(n => n != null).ToList();

            // Skip if this node is already a FirstOf node
            if (node.NodeType == "core.firstof")
                continue;

            // Check if the incoming branches come from DIFFERENT paths of a conditional node
            // This is the key: we need to detect if branches diverged from if/switch and reconverge
            var branchSources = new HashSet<string>();
            foreach (var conn in incomingConnections)
            {
                var conditionalAncestor = FindConditionalAncestor(workflow, conn.From, conn.FromPort);
                if (conditionalAncestor.HasValue)
                {
                    // Track the unique conditional node + port combination
                    branchSources.Add($"{conditionalAncestor.Value.NodeId}:{conditionalAncestor.Value.Port}");
                }
            }

            // Only warn if multiple DIFFERENT branches from the SAME conditional node converge
            if (branchSources.Count <= 1)
                continue;

            var nodeName = GetNodeDisplayName(node);
            var sourceNames = sourceNodes.Select(n => GetNodeDisplayName(n!)).ToList();

            issues.Add(
                new WorkflowValidationIssue
                {
                    Code = "MULTIPLE_BRANCHES_WITHOUT_JOIN",
                    Severity = ValidationSeverity.Warning,
                    NodeId = node.Id,
                    RelatedNodeIds = sourceNodes.Select(n => n!.Id).ToList(),
                    Title = "Multiple conditional branches converge without a FirstOf node",
                    Description =
                        $"Node '{nodeName}' receives inputs from {incomingConnections.Count} different branches ({string.Join(", ", sourceNames)}). "
                        + "When branches from conditional nodes (if, switch) converge on a single node, the workflow engine might wait for ALL inputs before executing, "
                        + "which can cause unexpected blocking behavior.",
                    Impact = "⚠️ The workflow may not execute as expected:\n" + "- If only one branch executes, the node will wait indefinitely for the other branches\n" + "- This can cause the workflow to appear 'stuck' or hang\n" + "- Data from only one branch will be used, others will be lost",
                    Suggestion =
                        "💡 Use a core.firstof node to properly merge the branches:\n\n"
                        + "1. Add a 'core.firstof' node between the branches and this node\n"
                        + "2. Connect all branch outputs to different inputs of the FirstOf node (input1, input2, etc.)\n"
                        + "3. Configure the FirstOf mode:\n"
                        + "   - 'passThrough': uses the first branch that executes (default)\n"
                        + "   - 'preferFirst': prefers input1, then input2, etc.\n"
                        + "   - 'preferNonNull': uses the first non-empty value\n"
                        + "4. Set 'wait_for_all' to false (default)\n"
                        + "5. Connect the FirstOf output to this node",
                    Example = "Pattern:\n" + "  storage_read ──┐\n" + "                 ├─→ [FIRST OF] → suggest_links\n" + "  save_structure ─┘\n\n" + "FirstOf configuration:\n" + "{\n" + "  \"mode\": \"preferNonNull\",\n" + "  \"wait_for_all\": false\n" + "}",
                    DocumentationUrl = "/docs/nodes/core/firstof",
                }
            );
        }

        return issues;
    }

    /// <summary>
    /// Detects nodes that are not connected to the workflow graph.
    /// </summary>
    private List<WorkflowValidationIssue> ValidateOrphanedNodes(WorkflowEntity workflow)
    {
        var issues = new List<WorkflowValidationIssue>();

        foreach (var node in workflow.Nodes)
        {
            // Skip resource nodes (they don't need incoming connections)
            if (IsResourceNode(node.NodeType))
                continue;

            var hasIncoming = workflow.Connections.Any(c => c.To == node.Id);
            var hasOutgoing = workflow.Connections.Any(c => c.From == node.Id);

            if (!hasIncoming && !hasOutgoing)
            {
                issues.Add(
                    new WorkflowValidationIssue
                    {
                        Code = "ORPHANED_NODE",
                        Severity = ValidationSeverity.Warning,
                        NodeId = node.Id,
                        Title = "Node is not connected to the workflow",
                        Description = $"Node '{GetNodeDisplayName(node)}' has no incoming or outgoing connections. " + "It will never execute and serves no purpose in the workflow.",
                        Impact = "This node will be ignored during workflow execution and wastes resources.",
                        Suggestion = "Either connect this node to the workflow or delete it if it's not needed.",
                        DocumentationUrl = "/docs/workflows/building-workflows",
                    }
                );
            }
        }

        return issues;
    }

    /// <summary>
    /// Validates that nodes requiring resources have them connected.
    /// </summary>
    private List<WorkflowValidationIssue> ValidateResourceConnections(WorkflowEntity workflow)
    {
        var issues = new List<WorkflowValidationIssue>();

        foreach (var node in workflow.Nodes)
        {
            // Check if node has resource input ports
            var resourcePorts = GetRequiredResourceInputPorts(node.NodeType);

            foreach (var portName in resourcePorts)
            {
                var hasResourceConnection = workflow.Connections.Any(c => c.To == node.Id && c.ToPort == portName);

                if (!hasResourceConnection)
                {
                    issues.Add(
                        new WorkflowValidationIssue
                        {
                            Code = "MISSING_RESOURCE_CONNECTION",
                            Severity = ValidationSeverity.Error,
                            NodeId = node.Id,
                            Title = $"Required resource '{portName}' not connected",
                            Description = $"Node '{GetNodeDisplayName(node)}' requires a '{portName}' resource connection to function properly.",
                            Impact = "❌ This node will fail to execute because it's missing a required resource.",
                            Suggestion = $"Connect a resource node (e.g., llm.model, storage.file) to the '{portName}' port.",
                            DocumentationUrl = $"/docs/nodes/{node.NodeType}",
                        }
                    );
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Detects potential deadlock situations with wait_for_all in conditional branches.
    /// </summary>
    private List<WorkflowValidationIssue> ValidatePotentialDeadlocks(WorkflowEntity workflow)
    {
        var issues = new List<WorkflowValidationIssue>();

        foreach (var node in workflow.Nodes)
        {
            if (node.NodeType != "core.firstof")
                continue;

            // Check if wait_for_all is enabled (Parameters is now JsonElement)
            var waitForAll = false;
            var waitParam = node.GetParameter("wait_for_all");
            if (waitParam.HasValue && waitParam.Value.ValueKind == JsonValueKind.True)
            {
                waitForAll = true;
            }

            if (!waitForAll)
                continue;

            // Check if inputs come from conditional branches
            var incomingConnections = workflow.Connections.Where(c => c.To == node.Id).ToList();

            var sourceNodes = incomingConnections.Select(c => workflow.Nodes.FirstOrDefault(n => n.Id == c.From)).Where(n => n != null).ToList();

            // Check if any path leads back to a conditional node
            var hasConditionalInPath = sourceNodes.Any(n => HasConditionalNodeInPath(workflow, n!.Id));

            if (hasConditionalInPath)
            {
                issues.Add(
                    new WorkflowValidationIssue
                    {
                        Code = "POTENTIAL_DEADLOCK",
                        Severity = ValidationSeverity.Warning,
                        NodeId = node.Id,
                        RelatedNodeIds = sourceNodes.Select(n => n!.Id).ToList(),
                        Title = "FirstOf node with 'wait_for_all' may cause deadlock",
                        Description = $"FirstOf node '{GetNodeDisplayName(node)}' has 'wait_for_all' enabled and receives inputs from conditional branches. " + "Since only one branch will execute, the firstof will wait forever for the other branches.",
                        Impact = "⚠️ The workflow will hang indefinitely waiting for branches that will never execute.",
                        Suggestion = "Set 'wait_for_all' to false (default) for conditional branch merging.",
                        Example = "Correct configuration:\n" + "{\n" + "  \"wait_for_all\": false  // Don't wait for all branches\n" + "}",
                        DocumentationUrl = "/docs/nodes/core/firstof",
                    }
                );
            }
        }

        return issues;
    }

    /// <summary>
    /// Validates conditional branch configurations.
    /// </summary>
    private List<WorkflowValidationIssue> ValidateConditionalBranches(WorkflowEntity workflow)
    {
        var issues = new List<WorkflowValidationIssue>();

        foreach (var node in workflow.Nodes)
        {
            if (!IsConditionalNode(node.NodeType))
                continue;

            var outgoingConnections = workflow.Connections.Where(c => c.From == node.Id).ToList();

            if (outgoingConnections.Count == 0)
            {
                issues.Add(
                    new WorkflowValidationIssue
                    {
                        Code = "CONDITIONAL_WITHOUT_BRANCHES",
                        Severity = ValidationSeverity.Info,
                        NodeId = node.Id,
                        Title = "Conditional node has no branches",
                        Description = $"{node.NodeType} node '{GetNodeDisplayName(node)}' has no outgoing connections. " + "The workflow will stop here regardless of the condition.",
                        Impact = "Workflow execution will end at this node.",
                        Suggestion = "Add at least one outgoing connection to continue the workflow based on the condition.",
                        DocumentationUrl = $"/docs/nodes/{node.NodeType}",
                    }
                );
            }
        }

        return issues;
    }

    /// <summary>
    /// Detects circular dependencies in the workflow graph.
    /// </summary>
    private List<WorkflowValidationIssue> ValidateCircularDependencies(WorkflowEntity workflow)
    {
        var issues = new List<WorkflowValidationIssue>();
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        foreach (var node in workflow.Nodes)
        {
            if (!visited.Contains(node.Id))
            {
                var cycle = DetectCycle(workflow, node.Id, visited, recursionStack, new List<string>());
                if (cycle != null && cycle.Count > 0)
                {
                    issues.Add(
                        new WorkflowValidationIssue
                        {
                            Code = "CIRCULAR_DEPENDENCY",
                            Severity = ValidationSeverity.Error,
                            NodeId = cycle[0],
                            RelatedNodeIds = cycle,
                            Title = "Circular dependency detected",
                            Description = $"A circular dependency was detected in the workflow: {string.Join(" → ", cycle)} → {cycle[0]}. " + "This will cause an infinite loop.",
                            Impact = "❌ The workflow will loop infinitely and never complete.",
                            Suggestion = "Remove one of the connections in the cycle or add a conditional exit point.",
                            DocumentationUrl = "/docs/workflows/best-practices",
                        }
                    );
                }
            }
        }

        return issues;
    }

    // Helper methods

    private bool IsConditionalNode(string nodeType)
    {
        return nodeType == "core.if" || nodeType == "core.switch";
    }

    private bool IsLoopNode(string nodeType)
    {
        // Check if the node is a loop/foreach control flow node
        if (_availableNodes.TryGetValue(nodeType, out var node))
        {
            return node is IControlFlowNode controlFlowNode && nodeType.Contains("foreach", StringComparison.OrdinalIgnoreCase);
        }

        // Fallback pattern matching for common loop node types
        return nodeType.Contains("foreach", StringComparison.OrdinalIgnoreCase) || nodeType.Contains("loop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the nearest conditional ancestor (if/switch) in the path.
    /// Returns the conditional node ID and the port it came from (true/false/etc).
    /// </summary>
    private (string NodeId, string Port)? FindConditionalAncestor(WorkflowEntity workflow, string nodeId, string? fromPort, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();

        // Avoid infinite loops
        if (visited.Contains(nodeId))
            return null;

        visited.Add(nodeId);

        var node = workflow.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return null;

        // If this is a conditional node, return it with the port
        if (IsConditionalNode(node.NodeType) && fromPort != null)
        {
            return (node.Id, fromPort);
        }

        // Otherwise, recurse to parent nodes (but NOT through foreach.loop - that's parallel!)
        // Follow all data flow connections, excluding resource connections
        var parentConnections = workflow.Connections.Where(c => c.To == nodeId && !IsResourcePort(node.NodeType, c.ToPort)).ToList();

        foreach (var conn in parentConnections)
        {
            // Skip connections from loop nodes - that's parallel execution, not conditional branching
            var sourceNode = workflow.Nodes.FirstOrDefault(n => n.Id == conn.From);
            if (sourceNode != null && IsLoopNode(sourceNode.NodeType))
                continue;

            var result = FindConditionalAncestor(workflow, conn.From, conn.FromPort, visited);
            if (result != null)
                return result;
        }

        return null;
    }

    private bool IsResourceNode(string nodeType)
    {
        // A resource node is a node that provides resources to other nodes
        // It has at least one Resource output port and typically no data flow inputs
        if (_availableNodes.TryGetValue(nodeType, out var node) && node is INodeMetadataProvider metadataProvider)
        {
            var hasResourceOutputs = metadataProvider.GetOutputPorts().Any(p => p.Kind == PortKind.Resource);

            return hasResourceOutputs;
        }

        // Fallback for nodes not in registry - assume it's not a resource node
        return false;
    }

    /// <summary>
    /// Checks if a specific port on a node is a resource port.
    /// </summary>
    private bool IsResourcePort(string nodeType, string portName)
    {
        if (_availableNodes.TryGetValue(nodeType, out var node) && node is INodeMetadataProvider metadataProvider)
        {
            var port = metadataProvider.GetInputPorts().FirstOrDefault(p => string.Equals(p.Name, portName, StringComparison.OrdinalIgnoreCase));

            return port?.Kind == PortKind.Resource;
        }

        // Fallback for nodes not in registry - assume it's not a resource port
        return false;
    }

    private string GetNodeDisplayName(WorkflowNodeDefinition node)
    {
        if (node.Metadata.TryGetValue("displayName", out var displayName))
            return displayName?.ToString() ?? node.Id;

        return node.Id;
    }

    private List<string> GetRequiredResourceInputPorts(string nodeType)
    {
        // Get resource ports from node metadata dynamically
        // This is now fully generic and uses the node's own metadata
        if (_availableNodes.TryGetValue(nodeType, out var node) && node is INodeMetadataProvider metadataProvider)
        {
            return metadataProvider.GetInputPorts().Where(p => p.Kind == PortKind.Resource && p.IsRequired).Select(p => p.Name).ToList();
        }

        // Node not found in registry - return empty list
        // This can happen for custom nodes or when the validator is used without DI
        return new List<string>();
    }

    private bool HasConditionalNodeInPath(WorkflowEntity workflow, string nodeId)
    {
        var visited = new HashSet<string>();
        return HasConditionalNodeInPathRecursive(workflow, nodeId, visited);
    }

    private bool HasConditionalNodeInPathRecursive(WorkflowEntity workflow, string nodeId, HashSet<string> visited)
    {
        if (visited.Contains(nodeId))
            return false;

        visited.Add(nodeId);

        var node = workflow.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return false;

        if (IsConditionalNode(node.NodeType))
            return true;

        // Check incoming connections
        var incomingConnections = workflow.Connections.Where(c => c.To == nodeId).ToList();

        foreach (var conn in incomingConnections)
        {
            if (HasConditionalNodeInPathRecursive(workflow, conn.From, visited))
                return true;
        }

        return false;
    }

    private List<string>? DetectCycle(WorkflowEntity workflow, string nodeId, HashSet<string> visited, HashSet<string> recursionStack, List<string> path)
    {
        visited.Add(nodeId);
        recursionStack.Add(nodeId);
        path.Add(nodeId);

        // Get outgoing connections
        var outgoingConnections = workflow.Connections.Where(c => c.From == nodeId).ToList();

        foreach (var conn in outgoingConnections)
        {
            var nextNodeId = conn.To;

            if (!visited.Contains(nextNodeId))
            {
                var cycle = DetectCycle(workflow, nextNodeId, visited, recursionStack, new List<string>(path));
                if (cycle != null)
                    return cycle;
            }
            else if (recursionStack.Contains(nextNodeId))
            {
                // Cycle detected
                var cycleStart = path.IndexOf(nextNodeId);
                return path.Skip(cycleStart).ToList();
            }
        }

        recursionStack.Remove(nodeId);
        return null;
    }
}
