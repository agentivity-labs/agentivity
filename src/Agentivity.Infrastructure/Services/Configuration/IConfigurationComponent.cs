namespace Agentivity.Infrastructure.Workflow.Services.Configuration;

/// <summary>
/// Interface for components that depend on configuration files.
/// Implementing classes will be automatically notified when their configuration file changes.
/// </summary>
public interface IConfigurationComponent
{
    /// <summary>
    /// Gets the relative path of the configuration file this component depends on.
    /// Example: "elementor/presets.json", "article/writer-options.json"
    /// </summary>
    string ConfigurationPath { get; }

    /// <summary>
    /// Reloads the configuration from disk.
    /// Called automatically when the configuration file is saved.
    /// </summary>
    void Reload();
}
