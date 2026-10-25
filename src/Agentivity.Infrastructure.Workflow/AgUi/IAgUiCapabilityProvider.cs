namespace Agentivity.Infrastructure.Workflow.AgUi;

/// <summary>
/// Implemented by workflow resource nodes that can contribute AG-UI widget bundles
/// and custom widget definitions to a run's execution context.
///
/// The coordinator discovers connected <see cref="IAgUiCapabilityProvider"/> nodes
/// via their <see cref="ResourceBinding"/> and propagates declarations into the
/// <c>RUN_STARTED</c> AG-UI event.
/// </summary>
public interface IAgUiCapabilityProvider
{
    /// <summary>
    /// Returns the IDs of the platform widget bundles activated by this node.
    /// <paramref name="binding"/> carries the resolved parameters for the node.
    /// </summary>
    IReadOnlyList<string> GetActiveBundleIds(IResourceBindingReader binding);

    /// <summary>
    /// Returns the custom (application-specific) widget definitions declared by this node.
    /// Returns an empty list if no custom widgets are configured.
    /// </summary>
    IReadOnlyList<AgUiCustomWidgetDefinition> GetCustomWidgets(IResourceBindingReader binding);
}

/// <summary>
/// Minimal read interface over a <c>ResourceBinding</c> exposed to capability providers.
/// </summary>
public interface IResourceBindingReader
{
    string? GetString(string key);
    IReadOnlyList<string> GetStringList(string key);
}

/// <summary>
/// An application-specific widget the LLM can invoke as a tool call.
/// The call is intercepted and emitted as a <c>CUSTOM_EVENT</c> AG-UI frame.
/// </summary>
/// <param name="Name">Tool name the LLM uses to invoke the widget (snake_case).</param>
/// <param name="Description">Human-readable description for the LLM.</param>
/// <param name="ParametersSchema">
/// The raw JSON of the schema object describing the widget's arguments. Declared as a JSON object on the
/// channel (never a string holding JSON); required, since a widget with no schema gives the agent nothing to fill.
/// </param>
public sealed record AgUiCustomWidgetDefinition(string Name, string Description, string? ParametersSchema);
