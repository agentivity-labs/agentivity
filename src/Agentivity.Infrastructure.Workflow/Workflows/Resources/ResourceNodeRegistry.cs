namespace Agentivity.Infrastructure.Workflows.Resources;

public static class ResourceNodeRegistry
{
    private static readonly Dictionary<string, string> ResourceTypes = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string nodeType, string resourceType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            throw new ArgumentException("Node type is required", nameof(nodeType));
        }

        if (string.IsNullOrWhiteSpace(resourceType))
        {
            throw new ArgumentException("Resource type is required", nameof(resourceType));
        }

        ResourceTypes[nodeType] = resourceType;
    }

    public static string? ResolveResourceType(string? nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            return null;
        }

        return ResourceTypes.TryGetValue(nodeType, out var value) ? value : null;
    }

    /// <summary>
    /// Determines the node class (action, resource, or display) based on the node type.
    /// </summary>
    public static string GetNodeClass(string? nodeType)
    {
        // If registered as a resource node → "resource"
        if (ResolveResourceType(nodeType) != null)
        {
            return NodeClass.Resource;
        }

        // If registered as a display node → "display" (Studio picks a resizable, custom-rendered
        // node wrapper for these — see DisplayNodeRegistry).
        if (DisplayNodeRegistry.IsDisplayNode(nodeType))
        {
            return NodeClass.Display;
        }

        // Default → "action" (all other nodes)
        return NodeClass.Action;
    }
}
