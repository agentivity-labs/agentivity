using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentivity.Infrastructure.Plugins;

/// <summary>
/// Extension methods for registering workflow plugins.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBuiltInWorkflowPlugin<TPlugin>(this IServiceCollection services)
        where TPlugin : class, IWorkflowPlugin, new() => services.AddWorkflowPlugin(new TPlugin());

    /// <summary>
    /// Adds a workflow plugin by loading it from an assembly file.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="pluginPath">Path to the plugin assembly</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddWorkflowPlugin(this IServiceCollection services, string pluginPath)
    {
        var plugin = PluginLoader.LoadFromAssembly(pluginPath);
        return AddWorkflowPlugin(services, plugin);
    }

    /// <summary>
    /// Adds a workflow plugin instance.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="plugin">The plugin instance</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddWorkflowPlugin(this IServiceCollection services, IWorkflowPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        services.TryAddSingleton<WorkflowPluginRuntimeHost>();

        // Register plugin-declared categories before nodes so that DefineMetadata
        // categoryId references are valid when nodes are instantiated.
        foreach (var category in plugin.GetCategories())
        {
            if (!NodeCategoryRegistry.Exists(category.Id))
                NodeCategoryRegistry.Register(category);
        }

        // Let the plugin configure its services
        plugin.ConfigureServices(services);

        services.AddSingleton(plugin);
        services.AddSingleton(typeof(IWorkflowPlugin), plugin);

        if (plugin is IEndpointContributorProvider endpointContributorProvider)
        {
            foreach (var endpointContributorType in endpointContributorProvider.GetEndpointContributorTypes())
            {
                services.AddSingleton(typeof(IEndpointContributor), endpointContributorType);
            }
        }

        foreach (var nodeType in plugin.GetNodeTypes())
        {
            services.AddSingleton(typeof(Node), nodeType);
        }

        return services;
    }

    public static IServiceCollection AddConfiguredWorkflowPlugins(this IServiceCollection services, IConfiguration configuration, string rootDirectory, Action<IWorkflowPlugin>? onPluginLoaded, Action<string, int>? onDiscoveryCompleted)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var pluginsDirectory = ResolvePluginsDirectory(configuration, rootDirectory);
        return services.AddWorkflowPlugins(pluginsDirectory, onPluginLoaded, onDiscoveryCompleted);
    }

    /// <summary>
    /// Discovers and loads all plugins from a directory.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="pluginsDirectory">Directory containing plugin assemblies (default: "_plugins")</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddWorkflowPlugins(this IServiceCollection services, string pluginsDirectory = "_plugins") =>
        services.AddWorkflowPlugins(
            pluginsDirectory,
            plugin => Console.WriteLine($"Loading plugin: {plugin.Name} v{plugin.Version}"),
            (directory, count) =>
            {
                if (count == 0)
                {
                    Console.WriteLine($"No plugins found in '{directory}'");
                    return;
                }

                Console.WriteLine($"Loaded {count} plugin(s)");
            }
        );

    public static IServiceCollection AddWorkflowPlugins(this IServiceCollection services, string pluginsDirectory, Action<IWorkflowPlugin>? onPluginLoaded, Action<string, int>? onDiscoveryCompleted)
    {
        var plugins = PluginLoader.DiscoverPlugins(pluginsDirectory);

        foreach (var plugin in plugins)
        {
            AddWorkflowPlugin(services, plugin);
            onPluginLoaded?.Invoke(plugin);
        }

        onDiscoveryCompleted?.Invoke(pluginsDirectory, plugins.Count);

        return services;
    }

    public static IEndpointRouteBuilder MapWorkflowPluginCapabilities(this IEndpointRouteBuilder endpoints)
    {
        endpoints.ServiceProvider.GetRequiredService<WorkflowPluginRuntimeHost>().MapCapabilities(endpoints);

        return endpoints;
    }

    public static string ResolvePluginsDirectory(IConfiguration configuration, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var pluginPath = configuration.GetSection("Plugins:StoragePath").Value ?? "_plugins/";
        return Path.Combine(rootDirectory, pluginPath);
    }
}
