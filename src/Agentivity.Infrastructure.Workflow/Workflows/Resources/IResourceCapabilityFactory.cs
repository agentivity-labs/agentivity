namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Factory for creating resource capability instances from resource bindings.
/// Resource nodes register factories for their capabilities, enabling pluggable resources.
///
/// External developers implement this to provide custom resource capabilities.
/// </summary>
public interface IResourceCapabilityFactory
{
    /// <summary>
    /// Type of capability this factory creates
    /// </summary>
    Type CapabilityType { get; }

    /// <summary>
    /// Creates a capability instance from a resource binding
    /// </summary>
    ValueTask<IResourceCapability> CreateAsync(ResourceBinding binding, NodeConfig context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic factory for creating resource capabilities with type safety
/// </summary>
public interface IResourceCapabilityFactory<TCapability> : IResourceCapabilityFactory
    where TCapability : IResourceCapability
{
    /// <summary>
    /// Creates a typed capability instance
    /// </summary>
    new ValueTask<TCapability> CreateAsync(ResourceBinding binding, NodeConfig context, CancellationToken cancellationToken = default);
}
