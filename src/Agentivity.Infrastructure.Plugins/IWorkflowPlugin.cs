using Agentivity.Domain.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace Agentivity.Infrastructure.Plugins;

/// <summary>
/// Interface for workflow plugins that provide additional nodes and services.
/// Plugins are loaded dynamically at runtime from separate assemblies.
/// </summary>
public interface IWorkflowPlugin
{
    /// <summary>
    /// Unique plugin identifier (e.g., "article", "emarketing")
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Human-readable plugin name
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Plugin version (semantic versioning recommended)
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Plugin description
    /// </summary>
    string? Description { get; }

    /// <summary>
    /// Configure services required by the plugin (e.g., HTTP clients, custom services)
    /// </summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Returns all workflow node types provided by this plugin.
    /// </summary>
    IEnumerable<Type> GetNodeTypes();

    /// <summary>
    /// Returns node categories declared by this plugin.
    /// Categories are registered before nodes so that <see cref="GetNodeTypes"/> can
    /// reference them via <c>DefineMetadata(..., categoryId)</c>.
    /// Default implementation returns an empty collection (no custom categories).
    /// </summary>
    IEnumerable<NodeCategory> GetCategories() => [];
}
