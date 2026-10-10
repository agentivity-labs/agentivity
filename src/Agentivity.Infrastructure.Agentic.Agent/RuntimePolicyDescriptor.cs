namespace Agentivity.Infrastructure.Agentic.Agent;

/// <summary>
/// Describes a single policy that a runtime makes available for user configuration.
///
/// Each runtime that wants its policies to be configurable via an AgentPolicyNode
/// declares a list of these descriptors. The policy node uses them to generate its
/// dynamic inputs when connected to an agent node running this runtime.
/// </summary>
/// <param name="Key">
/// Stable machine key used as the input name on the policy node.
/// Must be unique within a runtime's library. Example: "require_alternatives_before_join".
/// </param>
/// <param name="Label">Short human-readable label for the UI. Example: "Require alternatives before join".</param>
/// <param name="Description">Full explanation shown to the user in the UI.</param>
/// <param name="Kind">The input kind that will be created on the policy node.</param>
/// <param name="Default">Optional default value. Null means the node inherits platform defaults.</param>
public sealed record RuntimePolicyDescriptor(string Key, string Label, string Description, RuntimePolicyDescriptorKind Kind, object? Default = null);

/// <summary>The kind of UI input generated for a <see cref="RuntimePolicyDescriptor"/>.</summary>
public enum RuntimePolicyDescriptorKind
{
    /// <summary>Tri-state choice: inherit / enabled / disabled.</summary>
    Boolean,

    /// <summary>Free-form choice with runtime-defined options.</summary>
    Choice,

    /// <summary>Free-form text input.</summary>
    Text,
}
