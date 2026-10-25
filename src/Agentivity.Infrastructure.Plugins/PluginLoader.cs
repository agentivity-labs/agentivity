using System.Reflection;

namespace Agentivity.Infrastructure.Plugins;

/// <summary>
/// Loads workflow plugins from assemblies using <see cref="IsolatedPluginLoadContext"/>
/// so that each plugin's private dependencies cannot shadow the host's assemblies.
/// </summary>
public static class PluginLoader
{
    /// <summary>
    /// Loads a plugin from an assembly file.
    /// </summary>
    /// <param name="assemblyPath">Path to the plugin assembly (.dll)</param>
    /// <returns>The loaded plugin instance</returns>
    /// <exception cref="InvalidOperationException">If the assembly doesn't contain a valid plugin</exception>
    public static IWorkflowPlugin LoadFromAssembly(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Plugin assembly not found: {assemblyPath}");
        }

        // Each plugin loads in its own isolated context so that its private
        // dependencies (e.g. a newer OnnxRuntime or Npgsql) cannot shadow the
        // host's assemblies. Host contracts resolve to the default ALC.
        var loadContext = new IsolatedPluginLoadContext(assemblyPath);
        Assembly assembly;
        try
        {
            assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load assembly '{assemblyPath}': {ex.Message}", ex);
        }

        var pluginType = assembly.GetTypes().FirstOrDefault(t => typeof(IWorkflowPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

        if (pluginType == null)
        {
            throw new InvalidOperationException($"Assembly '{assemblyPath}' does not contain a class implementing IWorkflowPlugin.");
        }

        try
        {
            var instance = Activator.CreateInstance(pluginType);
            if (instance is not IWorkflowPlugin plugin)
            {
                throw new InvalidOperationException($"Failed to create plugin instance from type '{pluginType.FullName}'.");
            }

            return plugin;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to instantiate plugin type '{pluginType.FullName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Discovers and loads all plugins from a directory.
    /// </summary>
    /// <param name="pluginsDirectory">Directory containing plugin assemblies</param>
    /// <returns>List of loaded plugins</returns>
    public static IReadOnlyList<IWorkflowPlugin> DiscoverPlugins(string pluginsDirectory = "_plugins")
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            return Array.Empty<IWorkflowPlugin>();
        }

        var plugins = new List<IWorkflowPlugin>();
        var dllFiles = Directory.GetFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly);

        foreach (var dllPath in dllFiles)
        {
            try
            {
                var plugin = LoadFromAssembly(dllPath);
                plugins.Add(plugin);
            }
            catch (Exception ex)
            {
                // Log warning but continue loading other plugins
                Console.WriteLine($"Warning: Failed to load plugin from '{dllPath}': {ex.Message}");
            }
        }

        return plugins;
    }
}
