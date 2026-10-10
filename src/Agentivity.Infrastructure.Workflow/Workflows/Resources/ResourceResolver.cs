namespace Agentivity.Infrastructure.Workflows.Resources;

public static class ResourceResolver
{
    public static ResourceBinding Require(NodeConfig context, string portName, string resourceType)
    {
        var binding = TryResolve(context, portName, resourceType);
        if (binding is null)
        {
            throw new InvalidOperationException($"Node '{context.Node.Id}' requires a '{resourceType}' resource on port '{portName}'.");
        }

        return binding;
    }

    public static ResourceBinding? TryResolve(NodeConfig context, string portName, string resourceType)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var definition = context.Definition;
        var node = context.Node;
        var matches = definition.Connections.Where(c => string.Equals(c.To, node.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, portName, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (matches.Length == 0)
        {
            return null;
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"Node '{node.Id}' has multiple resources connected to port '{portName}'.");
        }

        var connection = matches[0];
        var sourceNode = definition.FindNode(connection.From) ?? throw new InvalidOperationException($"Resource node '{connection.From}' referenced by '{node.Id}' was not found.");

        var actualType = ResourceNodeRegistry.ResolveResourceType(sourceNode.NodeType) ?? throw new InvalidOperationException($"Node '{sourceNode.NodeType}' is not registered as a resource node.");

        // Support wildcard matching (e.g., "storage.*" matches "storage.file", "storage.supabase")
        if (!IsResourceTypeMatch(actualType, resourceType))
        {
            throw new InvalidOperationException($"Node '{node.Id}' expects resource type '{resourceType}' on port '{portName}' but received '{actualType}'.");
        }

        // Pass state for template resolution in resource parameters, and the graph itself so the
        // resolved resource node can in turn resolve its OWN resource input ports (see
        // TryResolveNestedToolNode).
        return new ResourceBinding(connection.From, connection.FromPort, sourceNode, actualType, context.State, context.Definition);
    }

    /// <summary>
    /// Checks if actualType matches expectedType, supporting wildcard patterns.
    /// Examples:
    ///   - "storage.file" matches "storage.file" (exact match)
    ///   - "storage.file" matches "storage.*" (wildcard match)
    ///   - "llm.model" matches "*" (match all)
    /// </summary>
    private static bool IsResourceTypeMatch(string actualType, string expectedType)
    {
        if (string.Equals(actualType, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            return true; // Exact match
        }

        // Wildcard matching
        if (expectedType.EndsWith("*", StringComparison.Ordinal))
        {
            var prefix = expectedType.Substring(0, expectedType.Length - 1);

            if (prefix == string.Empty)
            {
                return true; // "*" matches everything
            }

            return actualType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Resolves a resource capability from a port in a pluggable way.
    /// This enables any custom resource type to be used without modifying consumer code.
    ///
    /// Example:
    ///   var embedding = await ResourceResolver.GetCapabilityAsync&lt;IEmbeddingCapability&gt;(
    ///       context, "llm_memory", ResourceTypes.LlmMemory, cancellationToken);
    /// </summary>
    public static async ValueTask<TCapability?> GetCapabilityAsync<TCapability>(NodeConfig context, string portName, string resourceType, CancellationToken cancellationToken = default)
        where TCapability : class, IResourceCapability
    {
        var binding = TryResolve(context, portName, resourceType);
        if (binding == null)
        {
            return null;
        }

        return await ResourceCapabilityRegistry.CreateCapabilityAsync<TCapability>(binding, context, cancellationToken);
    }

    /// <summary>
    /// Resolves ALL resource bindings on a port that allows multiple connections.
    /// Returns empty list if no resources are connected.
    /// </summary>
    public static IReadOnlyList<ResourceBinding> ResolveAll(NodeConfig context, string portName, string resourceType)
    {
        ArgumentNullException.ThrowIfNull(context);

        var definition = context.Definition;
        var node = context.Node;
        var matches = definition.Connections.Where(c => string.Equals(c.To, node.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(c.ToPort, portName, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (matches.Length == 0)
        {
            return [];
        }

        var bindings = new List<ResourceBinding>(matches.Length);

        foreach (var connection in matches)
        {
            var sourceNode = definition.FindNode(connection.From) ?? throw new InvalidOperationException($"Resource node '{connection.From}' referenced by '{node.Id}' was not found.");

            var actualType = ResourceNodeRegistry.ResolveResourceType(sourceNode.NodeType) ?? throw new InvalidOperationException($"Node '{sourceNode.NodeType}' is not registered as a resource node.");

            if (!IsResourceTypeMatch(actualType, resourceType))
            {
                throw new InvalidOperationException($"Node '{node.Id}' expects resource type '{resourceType}' on port '{portName}' but received '{actualType}'.");
            }

            bindings.Add(new ResourceBinding(connection.From, connection.FromPort, sourceNode, actualType, context.State, context.Definition));
        }

        return bindings;
    }

    /// <summary>
    /// Resolves ALL capabilities of a given type from a multi-tool port.
    /// Skips tools that don't provide the requested capability.
    /// </summary>
    public static async ValueTask<IReadOnlyList<TCapability>> GetAllCapabilitiesAsync<TCapability>(NodeConfig context, string portName, string resourceType, CancellationToken cancellationToken = default)
        where TCapability : class, IResourceCapability
    {
        var bindings = ResolveAll(context, portName, resourceType);
        if (bindings.Count == 0)
        {
            return [];
        }

        var capabilities = new List<TCapability>();

        foreach (var binding in bindings)
        {
            var capability = await ResourceCapabilityRegistry.CreateCapabilityAsync<TCapability>(binding, context, cancellationToken);

            if (capability is not null)
            {
                capabilities.Add(capability);
            }
        }

        return capabilities;
    }

    /// <summary>
    /// Resolves all tool node instances implementing TInterface from a multi-tool port.
    /// Returns (nodeInstance, binding) pairs so the consumer can call the node directly with its config.
    /// No factory or capability registry involved — the node IS the tool.
    /// </summary>
    public static IReadOnlyList<(TInterface Tool, ResourceBinding Binding)> ResolveToolNodes<TInterface>(NodeConfig context, string portName, string resourceType)
        where TInterface : class
    {
        var bindings = ResolveAll(context, portName, resourceType);
        if (bindings.Count == 0)
        {
            return [];
        }

        var result = new List<(TInterface, ResourceBinding)>();

        foreach (var binding in bindings)
        {
            var tool = NodeInstanceRegistry.GetAs<TInterface>(binding.SourceNode.NodeType);
            if (tool is not null)
            {
                result.Add((tool, binding));
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves a single resource node implementing TInterface from a port.
    /// Returns null if no resource is connected.
    /// </summary>
    public static (TInterface Tool, ResourceBinding Binding)? TryResolveToolNode<TInterface>(NodeConfig context, string portName, string resourceType)
        where TInterface : class
    {
        var binding = TryResolve(context, portName, resourceType);
        if (binding is null)
            return null;

        var tool = NodeInstanceRegistry.GetAs<TInterface>(binding.SourceNode.NodeType);
        if (tool is not null)
            return (tool, binding);

        return null;
    }

    /// <summary>
    /// Resolves a single resource node implementing TInterface from a port.
    /// Throws if no resource is connected or the node doesn't implement the interface.
    /// </summary>
    public static (TInterface Tool, ResourceBinding Binding) RequireToolNode<TInterface>(NodeConfig context, string portName, string resourceType)
        where TInterface : class
    {
        var resolved = TryResolveToolNode<TInterface>(context, portName, resourceType);
        if (resolved is null)
            throw new InvalidOperationException($"Node '{context.Node.Id}' requires a resource implementing {typeof(TInterface).Name} on port '{portName}'.");

        return resolved.Value;
    }

    /// <summary>
    /// Resolves a resource node implementing TInterface connected to one of ANOTHER resource
    /// node's own input ports — for a resource node that itself needs a resource (e.g. a memory
    /// node with an optional "embeddings" port), called from inside a method that only received a
    /// <see cref="ResourceBinding"/> for that memory node, not a full <see cref="NodeConfig"/>.
    /// Returns null if <paramref name="ownBinding"/> carries no graph context (see
    /// <see cref="ResourceBinding.Definition"/>) or nothing is connected to that port.
    /// </summary>
    public static (TInterface Tool, ResourceBinding Binding)? TryResolveNestedToolNode<TInterface>(ResourceBinding ownBinding, string portName, string resourceType)
        where TInterface : class
    {
        ArgumentNullException.ThrowIfNull(ownBinding);

        if (ownBinding.Definition is null)
        {
            return null;
        }

        var nestedConfig = new NodeConfig(ownBinding.Definition, ownBinding.SourceNode, ownBinding.State ?? new Dictionary<string, object?>());
        return TryResolveToolNode<TInterface>(nestedConfig, portName, resourceType);
    }

    /// <summary>
    /// Requires a resource capability - throws if not found.
    /// </summary>
    public static async ValueTask<TCapability> RequireCapabilityAsync<TCapability>(NodeConfig context, string portName, string resourceType, CancellationToken cancellationToken = default)
        where TCapability : class, IResourceCapability
    {
        var capability = await GetCapabilityAsync<TCapability>(context, portName, resourceType, cancellationToken);
        if (capability == null)
        {
            throw new InvalidOperationException($"Node '{context.Node.Id}' requires a '{typeof(TCapability).Name}' capability from port '{portName}' but none was found.");
        }

        return capability;
    }
}
