namespace Agentivity.Infrastructure.Workflows.Resources;

/// <summary>
/// Marker interface for resource capabilities.
/// All resource capabilities must implement this interface.
/// This enables generic, type-safe capability discovery and instantiation.
///
/// External developers can create custom capabilities by implementing this interface.
/// </summary>
public interface IResourceCapability { }
