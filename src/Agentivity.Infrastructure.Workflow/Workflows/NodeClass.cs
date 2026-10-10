namespace Agentivity.Infrastructure.Workflows;

/// <summary>
/// Defines the class/category of a workflow node
/// </summary>
public static class NodeClass
{
    /// <summary>
    /// Action nodes - nodes that execute operations (default for most nodes)
    /// Includes business logic, control flow, and data manipulation nodes.
    /// </summary>
    public const string Action = "action";

    /// <summary>
    /// Resource nodes - nodes that provide shared configuration to other nodes
    /// These nodes don't execute, they just provide settings (e.g., LLM config, storage settings)
    /// </summary>
    public const string Resource = "resource";

    /// <summary>
    /// Display nodes - action nodes that pass their input straight through but render it
    /// directly on the node body (a console, an image, ...) instead of the standard
    /// icon/name tile. The Studio uses this to pick both the node's widget and its
    /// resizable graph-node wrapper — see <see cref="Resources.DisplayNodeRegistry"/>.
    /// </summary>
    public const string Display = "display";
}
