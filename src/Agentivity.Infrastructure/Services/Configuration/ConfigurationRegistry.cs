using System.Collections.Concurrent;

namespace Agentivity.Infrastructure.Workflow.Services.Configuration;

/// <summary>
/// Static registry for configuration components.
/// Components register themselves to receive automatic reload notifications when their config file changes.
/// Similar to CredentialsTemplateRegistry pattern.
/// </summary>
public static class ConfigurationRegistry
{
    private static readonly ConcurrentDictionary<string, List<IConfigurationComponent>> ComponentsByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    /// <summary>
    /// Registers a configuration component.
    /// The component will be automatically reloaded when its configuration file is saved.
    /// </summary>
    public static void Register(IConfigurationComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (string.IsNullOrWhiteSpace(component.ConfigurationPath))
        {
            throw new ArgumentException("Configuration path is required.", nameof(component));
        }

        lock (Lock)
        {
            if (!ComponentsByPath.TryGetValue(component.ConfigurationPath, out var components))
            {
                components = new List<IConfigurationComponent>();
                ComponentsByPath[component.ConfigurationPath] = components;
            }

            if (!components.Contains(component))
            {
                components.Add(component);
            }
        }
    }

    /// <summary>
    /// Unregisters a configuration component.
    /// </summary>
    public static void Unregister(IConfigurationComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        lock (Lock)
        {
            if (ComponentsByPath.TryGetValue(component.ConfigurationPath, out var components))
            {
                components.Remove(component);

                if (components.Count == 0)
                {
                    ComponentsByPath.TryRemove(component.ConfigurationPath, out _);
                }
            }
        }
    }

    /// <summary>
    /// Notifies all components that depend on the specified configuration file to reload.
    /// Called automatically by ConfigurationService after saving a file.
    /// </summary>
    public static void NotifyConfigurationChanged(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        // Normalize path separators
        var normalizedPath = relativePath.Replace("\\", "/");

        lock (Lock)
        {
            if (ComponentsByPath.TryGetValue(normalizedPath, out var components))
            {
                foreach (var component in components.ToList()) // ToList to avoid modification during iteration
                {
                    try
                    {
                        component.Reload();
                    }
                    catch
                    {
                        // Log but don't throw - one component failure shouldn't block others
                        // Logger would be injected if this wasn't static
                    }
                }
            }
        }
    }

    /// <summary>
    /// Notifies ALL registered components to reload their configuration.
    /// </summary>
    public static void NotifyAllConfigurationsChanged()
    {
        lock (Lock)
        {
            foreach (var components in ComponentsByPath.Values)
            {
                foreach (var component in components.ToList())
                {
                    try
                    {
                        component.Reload();
                    }
                    catch
                    {
                        // Log but don't throw
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets all registered components for a specific configuration file.
    /// </summary>
    public static IReadOnlyList<IConfigurationComponent> GetComponents(string relativePath)
    {
        var normalizedPath = relativePath.Replace("\\", "/");

        lock (Lock)
        {
            return ComponentsByPath.TryGetValue(normalizedPath, out var components) ? components.ToList() : Array.Empty<IConfigurationComponent>();
        }
    }

    /// <summary>
    /// Clears all registered components (for testing).
    /// </summary>
    public static void Clear()
    {
        lock (Lock)
        {
            ComponentsByPath.Clear();
        }
    }
}
