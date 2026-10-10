using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Central registry for resource capability factories.
/// Enables pluggable resource system - external developers can register new capabilities.
///
/// Usage:
/// 1. Create a capability interface (implements IResourceCapability)
/// 2. Create a factory (implements IResourceCapabilityFactory)
/// 3. Register the factory: ResourceCapabilityRegistry.RegisterFactory(nodeType, factory)
/// 4. Consumer nodes use: ResourceResolver.GetCapabilityAsync&lt;TCapability&gt;(...)
/// </summary>
public static class ResourceCapabilityRegistry
{
    private static readonly ConcurrentDictionary<string, IResourceCapabilityFactory> _factories = new();

    /// <summary>
    /// Registers a capability factory for a specific node type
    /// </summary>
    public static void RegisterFactory(string nodeType, IResourceCapabilityFactory factory)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            throw new ArgumentException("Node type cannot be null or empty", nameof(nodeType));
        }

        if (factory == null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        _factories[nodeType] = factory;
    }

    /// <summary>
    /// Gets a capability factory for a node type
    /// </summary>
    public static IResourceCapabilityFactory? GetFactory(string nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
        {
            return null;
        }

        _factories.TryGetValue(nodeType, out var factory);
        return factory;
    }

    /// <summary>
    /// Creates a capability instance from a resource binding
    /// </summary>
    public static async ValueTask<TCapability?> CreateCapabilityAsync<TCapability>(ResourceBinding binding, NodeConfig context, CancellationToken cancellationToken = default)
        where TCapability : class, IResourceCapability
    {
        if (binding == null || context == null)
        {
            return null;
        }

        var factory = GetFactory(binding.SourceNode.NodeType);
        if (factory == null)
        {
            return null;
        }

        try
        {
            var capability = await factory.CreateAsync(binding, context, cancellationToken);
            return capability as TCapability;
        }
        catch
        {
            // Factory might not support this capability type
            return null;
        }
    }
}
